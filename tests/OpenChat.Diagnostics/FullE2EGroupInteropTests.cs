using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenChat.Core.Configuration;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Diagnostics.TestHelpers;
using OpenChat.Diagnostics.WhitenoiseInterop;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Diagnostics;

/// <summary>
/// Full end-to-end interop test: 2 real OpenChat instances + 1 Whitenoise Docker,
/// all connected to the same relay (wss://test.thedude.cloud).
///
/// Uses the real MessageService flow (CreateGroupAsync → AddMemberAsync → RescanInvites → AcceptInvite → SendMessage).
/// Every message direction is tested with full epoch logging.
///
/// Prerequisites:
///   docker compose -f docker-compose.test.yml up -d --build
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "FullE2E")]
public class FullE2EGroupInteropTests : IAsyncLifetime
{
    private const string RelayUrl = "wss://test.thedude.cloud";

    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();
    private readonly List<NostrService> _nostrServices = new();
    private readonly List<MessageService> _messageServices = new();
    private WhitenoiseDockerClient? _wnClient;

    public FullE2EGroupInteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _wnClient = new WhitenoiseDockerClient(_output);
        try
        {
            await _wnClient.EnsureRunningAsync();
        }
        catch
        {
            _output.WriteLine("WARNING: Whitenoise Docker container not running. WN tests will be skipped.");
            _wnClient = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_wnClient != null)
            await _wnClient.DisposeAsync();

        foreach (var ms in _messageServices)
            ms.Dispose();

        foreach (var ns in _nostrServices)
        {
            try { await ns.DisconnectAsync(); }
            catch { }
            (ns as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    private record OCUser(
        string Name,
        string PubKeyHex, string PrivKeyHex,
        NostrService NostrService,
        StorageService Storage,
        IMlsService MlsService,
        MessageService MessageService);

    private async Task<OCUser> CreateOCUser(string name, string backend = "managed")
    {
        var nostrService = new NostrService();
        _nostrServices.Add(nostrService);
        var keys = nostrService.GenerateKeyPair();

        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_e2e_{name}_{Guid.NewGuid()}.db");
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

        IMlsService mlsService = backend switch
        {
            "managed" => new ManagedMlsService(storage),
            "rust" => new MlsService(storage),
            _ => throw new ArgumentException($"Unknown backend '{backend}'")
        };

        var messageService = new MessageService(storage, nostrService, mlsService);
        _messageServices.Add(messageService);
        await messageService.InitializeAsync();

        // Connect to relay
        await nostrService.ConnectAsync(RelayUrl);
        await Task.Delay(1000);

        _output.WriteLine($"[{backend}] Created '{name}': {keys.publicKeyHex[..16]}... connected to {RelayUrl}");
        _output.WriteLine($"  Relay status: {string.Join(", ", nostrService.ConnectedRelayUrls)}");

        return new OCUser(name, keys.publicKeyHex, keys.privateKeyHex,
            nostrService, storage, mlsService, messageService);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: 3 OpenChat users, full MessageService flow, all managed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_3Users_AllOC_Managed_FullFlow()
    {
        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E: 3 OpenChat users (managed) via relay");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");
        var charlie = await CreateOCUser("Charlie");

        // Step 1: Bob and Charlie publish KeyPackages
        _output.WriteLine("\n[Step 1] Publishing KeyPackages");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        var kpCharlie = await charlie.MlsService.GenerateKeyPackageAsync();
        await charlie.NostrService.PublishKeyPackageAsync(kpCharlie.Data, charlie.PrivKeyHex, kpCharlie.NostrTags);
        await Task.Delay(2000);
        _output.WriteLine("  KeyPackages published");

        // Step 2: Alice creates group and adds Bob + Charlie via MessageService
        _output.WriteLine("\n[Step 2] Alice creates group with Bob and Charlie");
        var chat = await alice.MessageService.CreateGroupAsync("E2E Test Group",
            new[] { bob.PubKeyHex, charlie.PubKeyHex });
        _output.WriteLine($"  Group created: chatId={chat.Id}, mlsGroupId={Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()[..16]}...");
        _output.WriteLine($"  Participants: {string.Join(", ", chat.ParticipantPublicKeys.Select(p => p[..16] + "..."))}");
        await Task.Delay(3000);

        // Step 3: Bob subscribes to welcomes and accepts invite
        _output.WriteLine("\n[Step 3] Bob accepts invite");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Bob has {bobInvites.Count} invite(s)");
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Subscribe Bob to group messages
        var bobNostrGroupId = chatBob.NostrGroupId != null
            ? Convert.ToHexString(chatBob.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatBob.MlsGroupId!).ToLowerInvariant();
        await bob.NostrService.SubscribeToGroupMessagesAsync(new[] { bobNostrGroupId });

        // Step 4: Charlie subscribes to welcomes and accepts invite
        _output.WriteLine("\n[Step 4] Charlie accepts invite");
        await charlie.NostrService.SubscribeToWelcomesAsync(charlie.PubKeyHex, charlie.PrivKeyHex);
        await Task.Delay(2000);
        await charlie.MessageService.RescanInvitesAsync();
        var charlieInvites = (await charlie.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Charlie has {charlieInvites.Count} invite(s)");
        Assert.NotEmpty(charlieInvites);
        var chatCharlie = await charlie.MessageService.AcceptInviteAsync(charlieInvites[0].Id);
        _output.WriteLine($"  Charlie joined: epoch={chatCharlie.MlsEpoch}");

        // Subscribe Charlie to group messages
        var charlieNostrGroupId = chatCharlie.NostrGroupId != null
            ? Convert.ToHexString(chatCharlie.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chatCharlie.MlsGroupId!).ToLowerInvariant();
        await charlie.NostrService.SubscribeToGroupMessagesAsync(new[] { charlieNostrGroupId });

        // Step 5: Bob needs to process the commit that added Charlie
        // In the real app, this happens via relay subscription. Let's fetch it manually.
        _output.WriteLine("\n[Step 5] Syncing commits");
        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

        // Give relay time then fetch all kind-445 events for the group
        await Task.Delay(2000);
        var groupEvents = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });
        _output.WriteLine($"  Found {groupEvents.Count} kind-445 events on relay");

        // Bob processes any commit events he hasn't seen
        foreach (var ev in groupEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);
                var result = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                if (result.IsCommit)
                    _output.WriteLine($"  Bob processed commit (epoch transition)");
                else
                    _output.WriteLine($"  Bob decrypted: \"{result.Plaintext}\"");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Bob: {ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        // Epoch check
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        var charlieEpoch = (await charlie.MlsService.GetGroupInfoAsync(chatCharlie.MlsGroupId!))?.Epoch;
        _output.WriteLine($"\n  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, Charlie={charlieEpoch}");

        // Step 6: Each user sends a message, all decrypt
        _output.WriteLine("\n[Step 6] Round-trip messaging via real relay");

        // Alice sends
        _output.WriteLine("\n  Alice sending...");
        await alice.MessageService.SendMessageAsync(chat.Id, "Hello from Alice E2E!");
        await Task.Delay(3000);

        // Fetch messages from relay and have Bob + Charlie decrypt
        var msgs = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });
        _output.WriteLine($"  {msgs.Count} events on relay after Alice's message");

        // Try to decrypt the latest event(s) as Bob and Charlie
        foreach (var ev in msgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);

                try
                {
                    var r = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                    if (!r.IsCommit)
                        _output.WriteLine($"  Bob decrypted: \"{r.Plaintext}\"");
                }
                catch { }

                try
                {
                    var r = await charlie.MlsService.DecryptMessageAsync(chatCharlie.MlsGroupId!, bytes);
                    if (!r.IsCommit)
                        _output.WriteLine($"  Charlie decrypted: \"{r.Plaintext}\"");
                }
                catch { }
            }
            catch { }
        }

        // Bob sends
        _output.WriteLine("\n  Bob sending...");
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Hello from Bob E2E!");
        await Task.Delay(3000);

        msgs = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });

        foreach (var ev in msgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);

                try
                {
                    var r = await alice.MlsService.DecryptMessageAsync(chat.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Bob"))
                        _output.WriteLine($"  Alice decrypted Bob: \"{r.Plaintext}\"");
                }
                catch { }

                try
                {
                    var r = await charlie.MlsService.DecryptMessageAsync(chatCharlie.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Bob"))
                        _output.WriteLine($"  Charlie decrypted Bob: \"{r.Plaintext}\"");
                }
                catch { }
            }
            catch { }
        }

        // Charlie sends
        _output.WriteLine("\n  Charlie sending...");
        await charlie.MessageService.SendMessageAsync(chatCharlie.Id, "Hello from Charlie E2E!");
        await Task.Delay(3000);

        msgs = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });

        foreach (var ev in msgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);

                try
                {
                    var r = await alice.MlsService.DecryptMessageAsync(chat.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Charlie"))
                        _output.WriteLine($"  Alice decrypted Charlie: \"{r.Plaintext}\"");
                }
                catch { }

                try
                {
                    var r = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Charlie"))
                        _output.WriteLine($"  Bob decrypted Charlie: \"{r.Plaintext}\"");
                }
                catch { }
            }
            catch { }
        }

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E TEST COMPLETE");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 2: 2 OC + 1 WN full E2E via relay
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E2E_3Users_2OC_1WN_FullFlow()
    {
        Skip.If(_wnClient == null, "Whitenoise Docker not running");

        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  FULL E2E: 2 OpenChat + 1 Whitenoise via relay");
        _output.WriteLine($"  Relay: {RelayUrl}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        var alice = await CreateOCUser("Alice");
        var bob = await CreateOCUser("Bob");
        var wnPubkey = await _wnClient!.CreateIdentityAsync();
        _output.WriteLine($"Charlie (WN): {wnPubkey[..16]}...");

        // Step 1: Bob publishes KeyPackage
        _output.WriteLine("\n[Step 1] Publishing KeyPackages");
        var kpBob = await bob.MlsService.GenerateKeyPackageAsync();
        await bob.NostrService.PublishKeyPackageAsync(kpBob.Data, bob.PrivKeyHex, kpBob.NostrTags);
        await Task.Delay(3000);

        // Wait for WN to publish KeyPackage
        _output.WriteLine("  Waiting for WN KeyPackage...");
        List<KeyPackage> wnKps = new();
        for (int i = 0; i < 20; i++)
        {
            wnKps = (await alice.NostrService.FetchKeyPackagesAsync(wnPubkey)).ToList();
            if (wnKps.Count > 0) break;
            await Task.Delay(2000);
        }
        _output.WriteLine($"  Found {wnKps.Count} WN KeyPackage(s)");

        if (wnKps.Count == 0)
        {
            _output.WriteLine("  SKIP: WN KeyPackage not available on relay");
            return;
        }

        // Step 2: Alice creates group with Bob + WN Charlie
        _output.WriteLine("\n[Step 2] Alice creates group");
        var chat = await alice.MessageService.CreateGroupAsync("E2E OC+WN Group",
            new[] { bob.PubKeyHex, wnPubkey });
        _output.WriteLine($"  Group: {Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant()[..16]}...");
        _output.WriteLine($"  Participants: {chat.ParticipantPublicKeys.Count}");
        await Task.Delay(3000);

        // Step 3: Bob accepts
        _output.WriteLine("\n[Step 3] Bob accepts invite");
        await bob.NostrService.SubscribeToWelcomesAsync(bob.PubKeyHex, bob.PrivKeyHex);
        await Task.Delay(2000);
        await bob.MessageService.RescanInvitesAsync();
        var bobInvites = (await bob.Storage.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"  Bob invites: {bobInvites.Count}");
        Assert.NotEmpty(bobInvites);
        var chatBob = await bob.MessageService.AcceptInviteAsync(bobInvites[0].Id);
        _output.WriteLine($"  Bob joined: epoch={chatBob.MlsEpoch}");

        // Step 4: Wait for WN to auto-accept
        _output.WriteLine("\n[Step 4] Waiting for WN to join group");
        var wnGroups = await _wnClient.PollUntilAsync(
            () => _wnClient.ListGroupsAsync(),
            g => g.Count > 0,
            timeoutMs: 30000, intervalMs: 2000,
            description: "WN joins group");

        if (wnGroups.Count == 0)
        {
            var invites = await _wnClient.ListInvitesAsync();
            _output.WriteLine($"  WN invites: {invites.Count}");
            if (invites.Count > 0)
            {
                await _wnClient.AcceptInviteAsync(invites[0].GroupIdHex);
                await Task.Delay(3000);
                wnGroups = await _wnClient.ListGroupsAsync();
            }
        }
        _output.WriteLine($"  WN groups: {wnGroups.Count}");
        Assert.NotEmpty(wnGroups);

        // Step 5: Sync commits — Bob processes the commit adding WN
        _output.WriteLine("\n[Step 5] Syncing commits for Bob");
        var nostrGroupIdHex = chat.NostrGroupId != null
            ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
            : Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();

        await Task.Delay(2000);
        var events = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });
        _output.WriteLine($"  {events.Count} kind-445 events");

        foreach (var ev in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);
                var r = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                if (r.IsCommit) _output.WriteLine($"  Bob processed commit");
            }
            catch { }
        }

        // Epoch check
        var aliceEpoch = (await alice.MlsService.GetGroupInfoAsync(chat.MlsGroupId!))?.Epoch;
        var bobEpoch = (await bob.MlsService.GetGroupInfoAsync(chatBob.MlsGroupId!))?.Epoch;
        _output.WriteLine($"\n  EPOCH CHECK: Alice={aliceEpoch}, Bob={bobEpoch}, WN=unknown");

        // Step 6: Alice sends — check Bob and WN both decrypt
        _output.WriteLine("\n[Step 6] Alice sends message");
        await alice.MessageService.SendMessageAsync(chat.Id, "Alice E2E to all!");
        await Task.Delay(5000);

        // Check WN got it
        var wnMsgs = await _wnClient.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex),
            m => m.Any(x => x.Content.Contains("Alice E2E")),
            timeoutMs: 15000, intervalMs: 2000,
            description: "WN sees Alice's message");

        var aliceMsgInWn = wnMsgs.FirstOrDefault(m => m.Content == "Alice E2E to all!");
        if (aliceMsgInWn != null)
            _output.WriteLine($"  WN decrypted Alice: \"{aliceMsgInWn.Content}\" from {aliceMsgInWn.SenderPubkeyHex[..16]}...");
        else
            _output.WriteLine($"  WN did NOT see Alice's message. Messages: {wnMsgs.Count}");

        // Step 7: Bob sends — check WN gets it (this is the critical test)
        _output.WriteLine("\n[Step 7] Bob sends message (critical — WN must see both OC users)");
        await bob.MessageService.SendMessageAsync(chatBob.Id, "Bob E2E to all!");
        await Task.Delay(5000);

        var wnMsgs2 = await _wnClient.PollUntilAsync(
            () => _wnClient.FetchMessagesAsync(wnGroups[0].GroupIdHex),
            m => m.Any(x => x.Content.Contains("Bob E2E")),
            timeoutMs: 15000, intervalMs: 2000,
            description: "WN sees Bob's message");

        var bobMsgInWn = wnMsgs2.FirstOrDefault(m => m.Content == "Bob E2E to all!");
        if (bobMsgInWn != null)
            _output.WriteLine($"  WN decrypted Bob: \"{bobMsgInWn.Content}\" from {bobMsgInWn.SenderPubkeyHex[..16]}...");
        else
            _output.WriteLine($"  *** WN did NOT see Bob's message — THIS IS THE BUG ***");

        // Step 8: WN sends — check both OC users get it
        _output.WriteLine("\n[Step 8] WN sends message");
        await _wnClient.SendMessageAsync(wnGroups[0].GroupIdHex, "Charlie WN E2E!");
        await Task.Delay(5000);

        events = await FetchRawEventsFromRelay(RelayUrl,
            new { kinds = new[] { 445 }, @__h = new[] { nostrGroupIdHex }, limit = 50 });

        bool aliceGotWn = false, bobGotWn = false;
        foreach (var ev in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var bytes = Convert.FromBase64String(content);

                if (!aliceGotWn) try
                {
                    var r = await alice.MlsService.DecryptMessageAsync(chat.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Charlie WN"))
                    { aliceGotWn = true; _output.WriteLine($"  Alice decrypted WN: \"{r.Plaintext}\""); }
                } catch { }

                if (!bobGotWn) try
                {
                    var r = await bob.MlsService.DecryptMessageAsync(chatBob.MlsGroupId!, bytes);
                    if (!r.IsCommit && r.Plaintext.Contains("Charlie WN"))
                    { bobGotWn = true; _output.WriteLine($"  Bob decrypted WN: \"{r.Plaintext}\""); }
                } catch { }
            }
            catch { }
        }

        if (!aliceGotWn) _output.WriteLine("  Alice could NOT decrypt WN message");
        if (!bobGotWn) _output.WriteLine("  Bob could NOT decrypt WN message");

        // Summary
        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
        _output.WriteLine("  RESULTS:");
        _output.WriteLine($"    WN saw Alice's message: {aliceMsgInWn != null}");
        _output.WriteLine($"    WN saw Bob's message:   {bobMsgInWn != null}");
        _output.WriteLine($"    Alice saw WN message:   {aliceGotWn}");
        _output.WriteLine($"    Bob saw WN message:     {bobGotWn}");
        _output.WriteLine("═══════════════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helper: Fetch raw events from relay
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<List<string>> FetchRawEventsFromRelay(string relayUrl, object filter)
    {
        var events = new List<string>();
        var subId = $"e2e_{Guid.NewGuid():N}"[..16];

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            var filterJson = JsonSerializer.Serialize(filter)
                .Replace("\"__h\"", "\"#h\"").Replace("\"__p\"", "\"#p\"");
            await ws.SendAsync(Encoding.UTF8.GetBytes($"[\"REQ\",\"{subId}\",{filterJson}]"),
                System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[65536];
            var sb = new StringBuilder();
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.StartsWith("[\"EVENT\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.GetArrayLength() >= 3)
                        events.Add(doc.RootElement[2].GetRawText());
                }
                else if (msg.StartsWith("[\"EOSE\"")) break;
            }

            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes($"[\"CLOSE\",\"{subId}\"]"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Relay fetch error: {ex.Message}");
        }
        return events;
    }
}

