using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Diagnostics.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Scramble.Diagnostics.RelayHarness;

/// <summary>
/// Drives Scramble's <see cref="MessageService.AddMemberAsync"/> against a single
/// <see cref="FaultyRelay"/> with fault knobs flipped to reproduce the
/// publish-failure handling gap discovered in NostrService:
///
///   PublishEventAsync (NostrService.cs:2972-2993) waits 5s for an OK,
///   then logs a warning and returns the event id REGARDLESS of whether
///   the OK actually arrived.
///
/// PublishCommitAsync (used by AddMemberAsync) does not check
/// LastPublishOkResult, so AddMember returns "successfully" even when no
/// relay confirmed the commit. The dedup marker, Welcome publish, and
/// participant update all still happen.
/// </summary>
[Trait("Category", "RelayHarness")]
public class PublishFailureTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();
    private FaultyRelay? _relay;

    public PublishFailureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _relay = new FaultyRelay();
        await _relay.StartAsync();
        _output.WriteLine($"FaultyRelay listening at {_relay.WsUrl}");
    }

    public async Task DisposeAsync()
    {
        foreach (var ms in _messageServices) ms.Dispose();
        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { }
            (ns as IDisposable)?.Dispose();
        }
        if (_relay != null) await _relay.DisposeAsync();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in _dbPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }

    private record ScrambleUser(
        string Name, string PubKeyHex, string PrivKeyHex, string Npub, string Nsec,
        NostrService NostrService, StorageService Storage, IMlsService MlsService,
        MessageService MessageService);

    private async Task<ScrambleUser> CreateUserAsync(string name)
    {
        var nostr = new NostrService();
        _nostrServices.Add(nostr);

        var keys = nostr.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"scramble_relay_fault_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);

        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub,
            Nsec = keys.nsec,
            DisplayName = name,
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        IMlsService mls = new ManagedMlsService(storage);
        var messages = new MessageService(storage, nostr, mls);
        _messageServices.Add(messages);
        await messages.InitializeAsync();

        _output.WriteLine($"Created '{name}': pubkey={keys.publicKeyHex[..16]}...");
        return new ScrambleUser(name, keys.publicKeyHex, keys.privateKeyHex,
            keys.npub, keys.nsec, nostr, storage, mls, messages);
    }

    [Fact]
    public async Task AddMember_OkDropped_StillReturnsSuccessfully_AndDedupMarkerIsWritten()
    {
        Assert.NotNull(_relay);

        // Setup: Alice and Bob, both connected to the FaultyRelay. Bob publishes
        // his KeyPackage (the relay accepts it normally for now — DropOk is off).
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        await alice.NostrService.ConnectAsync(_relay.WsUrl);
        await bob.NostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);

        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        var bobKpEventId = await bob.NostrService.PublishKeyPackageAsync(
            bobKp.Data, bob.PrivKeyHex, bobKp.NostrTags);
        _output.WriteLine($"Bob KP published normally: event={bobKpEventId[..16]}...");
        await Task.Delay(500);

        // Alice creates the group (no members yet, so no commit publish).
        var groupRelays = new[] { _relay.WsUrl };
        var groupInfo = await alice.MlsService.CreateGroupAsync("Fault Test Group", groupRelays);
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);
        var nostrGroupIdHex = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Fault Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chat);

        // ── The fault ──
        // From now on, every EVENT the relay accepts is stored + broadcast,
        // but no OK is sent back. Scramble's PublishEventAsync will time out
        // on the OK wait (5s) and return the event id with a warning.
        _relay.Faults.DropOk = true;
        _output.WriteLine("Fault enabled: DropOk = true");

        // ── The act ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? thrown = null;
        try
        {
            await alice.MessageService.AddMemberAsync(chat.Id, bob.PubKeyHex);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        sw.Stop();
        _output.WriteLine($"AddMember returned in {sw.ElapsedMilliseconds}ms, thrown={thrown?.GetType().Name ?? "<none>"}");

        // ── Assertions ──
        // 1. AddMember does NOT throw — this is the bug. A publish that no
        //    relay confirmed should not look like success.
        Assert.Null(thrown);

        // 2. The OK timeout is at least 5s (NostrService.cs:2973), so the call
        //    has to have waited for it (twice — commit and welcome).
        Assert.True(sw.ElapsedMilliseconds >= 5000,
            $"expected at least 5s wall time for OK timeout, got {sw.ElapsedMilliseconds}ms");

        // 3. NostrService recorded the publish as NOT accepted.
        Assert.False(alice.NostrService.LastPublishOkResult.accepted,
            "expected LastPublishOkResult.accepted to be false (no relay sent OK)");

        // 4. Despite the publish failure, the dedup marker for the commit was
        //    persisted (MessageService.cs:711-724 ran). This is what proves
        //    the post-publish code path executed as if everything was fine.
        var messages = await alice.Storage.GetMessagesForChatAsync(chat.Id);
        var commitMarker = messages.FirstOrDefault(m => m.Type == MessageType.System && m.Content == "[commit]");
        Assert.NotNull(commitMarker);
        _output.WriteLine($"Dedup marker written: event={commitMarker!.NostrEventId?[..16]}...");

        // 5. The participants list was updated as if Bob successfully joined.
        var aliceChat = await alice.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(aliceChat);
        Assert.Contains(bob.PubKeyHex, aliceChat!.ParticipantPublicKeys);

        // 6. The relay actually has the events stored — the wire-level publish
        //    succeeded, only the OK ack was missing.
        Assert.True(_relay.StoredEventCount >= 1,
            $"expected the relay to have stored at least the commit event, got {_relay.StoredEventCount}");

        _output.WriteLine($"Relay has {_relay.StoredEventCount} stored event(s) — wire publish landed; only OK was missing.");
        _output.WriteLine("BUG REPRODUCED: AddMember returned 'successfully' despite no relay confirmation.");
    }

    [Fact]
    public async Task AddMember_RelayBlackholesEvents_AliceThinksBobJoined_ButBobKnowsNothing()
    {
        Assert.NotNull(_relay);

        // Setup as before — KP publish happens cleanly before the fault is enabled.
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        await alice.NostrService.ConnectAsync(_relay.WsUrl);
        await bob.NostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);

        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(bobKp.Data, bob.PrivKeyHex, bobKp.NostrTags);
        await Task.Delay(500);

        // Bob also subscribes to his own welcome inbox so any inbound 1059
        // gift-wrap would land in his pending invites — proving that under
        // the fault, no welcome ever reached him.
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(500);

        var groupRelays = new[] { _relay.WsUrl };
        var groupInfo = await alice.MlsService.CreateGroupAsync("Blackhole Test Group", groupRelays);
        var nostrGroupId = alice.MlsService.GetNostrGroupId(groupInfo.GroupId);

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Blackhole Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { alice.PubKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await alice.Storage.SaveChatAsync(chat);

        var relayEventCountBefore = _relay.StoredEventCount;
        _output.WriteLine($"Pre-fault relay event count: {relayEventCountBefore}");

        // ── The fault ──
        // Strongest variant: the relay completely drops EVENTs. No OK, no store,
        // no broadcast. From Scramble's perspective: TCP send succeeded, OK never
        // arrived, code path keeps going as we saw in the previous test. From
        // Bob's perspective: nothing happened — no commit, no welcome, no event.
        _relay.Faults.BlackholeEvent = true;
        _output.WriteLine("Fault enabled: BlackholeEvent = true");

        await alice.MessageService.AddMemberAsync(chat.Id, bob.PubKeyHex);

        // Give Bob's subscription a moment to drain anything (nothing should land).
        await Task.Delay(2000);

        // ── Assertions ──
        // 1. Relay accepted no new events under the fault.
        Assert.Equal(relayEventCountBefore, _relay.StoredEventCount);
        _output.WriteLine($"Relay still has {_relay.StoredEventCount} event(s) — commit + welcome both blackholed.");

        // 2. Alice's local view says Bob joined the group.
        var aliceChat = await alice.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(aliceChat);
        Assert.Contains(bob.PubKeyHex, aliceChat!.ParticipantPublicKeys);
        var pubkeySnippets = string.Join(", ", aliceChat.ParticipantPublicKeys.Select(p => p[..16] + "..."));
        _output.WriteLine($"Alice's chat.ParticipantPublicKeys = [{pubkeySnippets}]");

        // 3. Alice's local MLS state advanced (epoch went forward as part of AddMembersAsync).
        Assert.True(aliceChat.MlsEpoch >= groupInfo.Epoch);

        // 4. Bob has NO knowledge of this group.
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        var bobChats = (await bob.Storage.GetAllChatsAsync()).ToList();
        _output.WriteLine($"Bob: {bobInvites.Count} pending invite(s), {bobChats.Count} chat(s)");
        Assert.Empty(bobInvites);
        Assert.Empty(bobChats);

        _output.WriteLine("BUG REPRODUCED: silent state divergence — Alice's local state says Bob joined; Bob is unaware.");
    }
}
