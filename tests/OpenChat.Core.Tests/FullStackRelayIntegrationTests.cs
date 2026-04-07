using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenChat.Core.Marmot;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;
using OpenChat.Core.Configuration;
using OpenChat.Core.Tests.TestHelpers;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// Full-stack integration tests using a real Docker relay (wss://test.thedude.cloud),
/// real NostrService, real StorageService, real MlsService, and real MessageService.
/// These tests prove the complete invite delivery pipeline works end-to-end.
///
/// Requires: docker compose -f docker-compose.test.yml up -d
/// </summary>
[Trait("Category", "Relay")]
public class FullStackRelayIntegrationTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://test.thedude.cloud";

    private readonly ITestOutputHelper _output;

    // User A (sender/inviter)
    private NostrService _nostrServiceA = null!;
    private StorageService _storageA = null!;
    private IMlsService _mlsServiceA = null!;
    private MessageService _messageServiceA = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _dbPathA = null!;

    // User B (receiver/invitee)
    private NostrService _nostrServiceB = null!;
    private StorageService _storageB = null!;
    private IMlsService _mlsServiceB = null!;
    private MessageService _messageServiceB = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;
    private string _dbPathB = null!;

    public FullStackRelayIntegrationTests(ITestOutputHelper output)
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task SetupUsers(string backend)
    {
        // ── User A setup ──
        _nostrServiceA = new NostrService();
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        _dbPathA = Path.Combine(Path.GetTempPath(), $"openchat_fullstack_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA, new MockSecureStorage());
        await _storageA.InitializeAsync();
        await _storageA.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyA,
            PrivateKeyHex = _privKeyA,
            Npub = $"npub1userA",
            Nsec = $"nsec1userA",
            DisplayName = "User A",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceA = CreateMlsService(backend, _storageA);
        _messageServiceA = new MessageService(_storageA, _nostrServiceA, _mlsServiceA);

        // ── User B setup ──
        _nostrServiceB = new NostrService();
        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        _dbPathB = Path.Combine(Path.GetTempPath(), $"openchat_fullstack_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB, new MockSecureStorage());
        await _storageB.InitializeAsync();
        await _storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyB,
            PrivateKeyHex = _privKeyB,
            Npub = $"npub1userB",
            Nsec = $"nsec1userB",
            DisplayName = "User B",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceB = CreateMlsService(backend, _storageB);
        _messageServiceB = new MessageService(_storageB, _nostrServiceB, _mlsServiceB);

        // Initialize MessageService for both users (subscribes to Events, inits MLS)
        await _messageServiceA.InitializeAsync();
        await _messageServiceB.InitializeAsync();

        // Connect both to the relay
        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);

        // Publish NIP-65 relay lists so FetchRelayListAsync finds them on localhost
        // instead of timing out trying to reach external discovery relays
        var relayPrefs = new List<RelayPreference>
        {
            new() { Url = RelayUrl, Usage = RelayUsage.Both }
        };
        await _nostrServiceA.PublishRelayListAsync(relayPrefs, _privKeyA);
        await _nostrServiceB.PublishRelayListAsync(relayPrefs, _privKeyB);

        await Task.Delay(1000); // Let connections stabilize
    }

    private static IMlsService CreateMlsService(string backend, IStorageService storage) => backend switch
    {
        "managed" => new ManagedMlsService(storage),
        "rust" => new MlsService(storage),
        _ => throw new ArgumentException($"Unknown backend '{backend}'. Use 'rust' or 'managed'.")
    };

    public async Task DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        if (_nostrServiceA != null) await _nostrServiceA.DisconnectAsync();
        if (_nostrServiceB != null) await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (_dbPathA != null) TryDeleteFile(_dbPathA);
        if (_dbPathB != null) TryDeleteFile(_dbPathB);
    }

    /// <summary>
    /// The critical test: User A publishes a Welcome (kind 444) to the real relay,
    /// User B has subscribed to welcomes, and MessageService should create a PendingInvite.
    /// This tests the FULL pipeline: publish → relay → WebSocket → Events observable →
    /// MessageService.OnNostrEventReceived → HandleWelcomeEventAsync → PendingInvite.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_WelcomePublished_ArriveAsPendingInvite(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: User B subscribes to welcomes via the real relay
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        _output.WriteLine("User B subscribed to welcomes");
        await Task.Delay(500);

        // Step 2: Set up observer for PendingInvite from MessageService
        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(
                invite =>
                {
                    _output.WriteLine($"PendingInvite received! Id={invite.Id}, Sender={invite.SenderPublicKey[..16]}...");
                    inviteTcs.TrySetResult(invite);
                },
                ex =>
                {
                    _output.WriteLine($"NewInvites error: {ex.Message}");
                    inviteTcs.TrySetException(ex);
                });

        // Also observe raw Events to see if the event arrives at NostrService level
        var rawEventTcs = new TaskCompletionSource<NostrEventReceived>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var rawSub = _nostrServiceB.Events
            .Where(e => e.Kind == 444)
            .Take(1)
            .Subscribe(
                e =>
                {
                    _output.WriteLine($"Raw kind-444 event received at NostrService! EventId={e.EventId[..16]}..., Sender={e.PublicKey[..16]}...");
                    rawEventTcs.TrySetResult(e);
                });

        // Step 3: User A publishes a Welcome for User B via the real relay
        var welcomeData = new byte[128];
        RandomNumberGenerator.Fill(welcomeData);

        _output.WriteLine("User A publishing Welcome...");
        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA);
        _output.WriteLine($"Welcome published with eventId: {eventId}");
        Assert.NotEmpty(eventId);
        Assert.Equal(64, eventId.Length);

        // Step 4: Verify the raw event arrives at NostrService level
        using var rawCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        rawCts.Token.Register(() => rawEventTcs.TrySetCanceled());

        var rawEvent = await rawEventTcs.Task;
        _output.WriteLine($"Raw event confirmed: kind={rawEvent.Kind}, pubkey={rawEvent.PublicKey[..16]}...");
        Assert.Equal(444, rawEvent.Kind);
        Assert.Equal(_pubKeyA, rawEvent.PublicKey);

        // Step 5: Verify the PendingInvite was created by MessageService
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var invite = await inviteTcs.Task;
        _output.WriteLine($"PendingInvite confirmed: sender={invite.SenderPublicKey[..16]}..., welcomeDataLen={invite.WelcomeData.Length}");

        Assert.Equal(_pubKeyA, invite.SenderPublicKey);
        // NostrEventId is now the rumor's own id (SHA-256 of canonical content),
        // not the gift wrap event id returned by PublishWelcomeAsync
        Assert.NotEmpty(invite.NostrEventId);
        Assert.Equal(64, invite.NostrEventId.Length);
        Assert.Equal(welcomeData, invite.WelcomeData);

        // Step 6: Verify invite was persisted to storage
        var storedInvites = (await _storageB.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"Stored invites count: {storedInvites.Count}");
        // NostrEventId is the rumor's own id, not the gift wrap event id
        Assert.Contains(storedInvites, i => !string.IsNullOrEmpty(i.NostrEventId) && i.NostrEventId.Length == 64);

        _output.WriteLine("FULL STACK TEST PASSED: Welcome → Relay → NostrService → MessageService → PendingInvite → Storage");
    }

    /// <summary>
    /// Test that RescanInvitesAsync (used by the Rescan button in UI) can find
    /// Welcome events that were published BEFORE the subscription was set up.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_RescanInvites_FindsHistoricalWelcomes(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: User A publishes a Welcome BEFORE User B subscribes
        var welcomeData = new byte[128];
        RandomNumberGenerator.Fill(welcomeData);

        _output.WriteLine("User A publishing Welcome (before User B subscribes)...");
        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA);
        _output.WriteLine($"Welcome published with eventId: {eventId}");

        await Task.Delay(1000); // Let relay store it

        // Step 2: User B rescans for invites (fetches historical events)
        _output.WriteLine("User B rescanning for invites...");
        await _messageServiceB.RescanInvitesAsync();

        // Step 3: Verify the invite was found and stored
        var storedInvites = (await _storageB.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"Stored invites after rescan: {storedInvites.Count}");

        foreach (var inv in storedInvites)
        {
            _output.WriteLine($"  Invite: sender={inv.SenderPublicKey[..16]}..., nostrEventId={inv.NostrEventId?[..16]}...");
        }

        Assert.NotEmpty(storedInvites);
        // NostrEventId is the rumor's own id, not the gift wrap event id
        Assert.Contains(storedInvites, i => !string.IsNullOrEmpty(i.NostrEventId) && i.NostrEventId.Length == 64);

        _output.WriteLine("RESCAN TEST PASSED: Historical Welcome found via FetchWelcomeEventsAsync");
    }

    /// <summary>
    /// Test that accepting an invite with random (non-MLS) welcome data throws
    /// "invalid welcome message" from the Rust/native MLS backend.
    /// The relay transport works fine — it's the MLS parsing that correctly rejects garbage.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_AcceptInvite_RandomBytes_ThrowsInvalidWelcome(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: Subscribe User B to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        // Step 2: Set up invite listener
        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // Step 3: User A publishes random bytes as Welcome (not real MLS data)
        var welcomeData = new byte[256];
        RandomNumberGenerator.Fill(welcomeData);
        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA);
        _output.WriteLine($"Welcome published: {eventId}");

        // Step 4: Wait for invite — relay transport should work
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());
        var invite = await inviteTcs.Task;
        _output.WriteLine($"Invite received: {invite.Id}");

        // Step 5: AcceptInvite should fail because random bytes aren't valid MLS Welcome data
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => _messageServiceB.AcceptInviteAsync(invite.Id));
        _output.WriteLine($"Correctly rejected random welcome ({ex.GetType().Name}): {ex.Message}");
    }

    /// <summary>
    /// Full end-to-end test: User A creates a real MLS group, adds User B using
    /// a real KeyPackage, publishes the Welcome through the relay, User B receives
    /// and accepts the invite, creating a real group chat.
    /// Both users use the Rust/native MLS backend.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task FullStack_AcceptInvite_RealMlsData_CreatesGroupChat(string backend)
    {
        await SetupUsers(backend);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: User B generates a real KeyPackage and publishes it to the relay
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64);
        _output.WriteLine($"User B generated KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        Assert.Equal(64, kpEventId.Length);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000); // Let relay store it

        // Step 2: User A creates a group
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Full Stack Test Group", new[] { "wss://relay.test" });
        Assert.NotNull(groupInfo.GroupId);
        _output.WriteLine($"User A created group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // Save chat record for User A
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Full Stack Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // Step 3: User A fetches User B's KeyPackage from the relay
        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        _output.WriteLine($"User A fetched KeyPackage from relay: {fetchedKPs[0].NostrEventId}");

        // Step 4: User A adds User B to the group (real MLS operation)
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"User A added User B: welcome={welcome.WelcomeData.Length} bytes");

        // Step 5: User B subscribes to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // Step 6: User A publishes the real Welcome through the relay
        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"Welcome published: {welcomeEventId}");

        // Step 7: Wait for invite
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());
        var invite = await inviteTcs.Task;
        _output.WriteLine($"Invite received: {invite.Id}");

        // Step 8: Accept the invite (real MLS ProcessWelcome)
        var chatB = await _messageServiceB.AcceptInviteAsync(invite.Id);
        _output.WriteLine($"Chat created: id={chatB.Id}, name={chatB.Name}, type={chatB.Type}");

        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);

        // Step 9: Verify invite was cleaned up
        var remaining = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.DoesNotContain(remaining, i => i.Id == invite.Id);

        // Step 10: Verify chat was persisted
        var savedChat = await _storageB.GetChatAsync(chatB.Id);
        Assert.NotNull(savedChat);
        Assert.Equal(ChatType.Group, savedChat!.Type);

        // Step 11: Verify bidirectional message exchange
        var ciphertextA = await _mlsServiceA.EncryptMessageAsync(groupInfo.GroupId, "Hello from A!");
        var decryptedByB = await _mlsServiceB.DecryptMessageAsync(chatB.MlsGroupId!, ciphertextA);
        Assert.Equal("Hello from A!", decryptedByB.Plaintext);
        _output.WriteLine($"A→B message verified: \"{decryptedByB.Plaintext}\"");

        var ciphertextB = await _mlsServiceB.EncryptMessageAsync(chatB.MlsGroupId!, "Hello from B!");
        var decryptedByA = await _mlsServiceA.DecryptMessageAsync(groupInfo.GroupId, ciphertextB);
        Assert.Equal("Hello from B!", decryptedByA.Plaintext);
        _output.WriteLine($"B→A message verified: \"{decryptedByA.Plaintext}\"");

        _output.WriteLine("FULL STACK TEST PASSED: KeyPackage → Relay → AddMember → Welcome → Relay → AcceptInvite → Chat → Messages");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Temp file - will be cleaned up eventually
        }
    }
}
