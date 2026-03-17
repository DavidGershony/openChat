using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// Real-relay integration tests that exercise the full A->B group chat flow
/// using real NostrService instances connected to a local relay (ws://localhost:7777).
///
/// These tests use the managed (C#) MLS backend only and require a running relay.
/// They are tagged with [Trait("Category", "Integration")] so CI can exclude them.
///
/// Requires: docker compose -f docker-compose.test.yml up -d  (relay on ws://localhost:7777)
/// </summary>
[Trait("Category", "Integration")]
public class RealRelayGroupChatTests : IAsyncLifetime
{
    private const string RelayUrl = "ws://localhost:7777";

    private readonly ITestOutputHelper _output;
    private readonly ILogger _logger;

    // User A context
    private NostrService _nostrServiceA = null!;
    private StorageService _storageA = null!;
    private ManagedMlsService _mlsServiceA = null!;
    private MessageService _messageServiceA = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _dbPathA = null!;

    // User B context
    private NostrService _nostrServiceB = null!;
    private StorageService _storageB = null!;
    private ManagedMlsService _mlsServiceB = null!;
    private MessageService _messageServiceB = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;
    private string _dbPathB = null!;

    private bool _relayAvailable;

    public RealRelayGroupChatTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = LoggingConfiguration.CreateLogger<RealRelayGroupChatTests>();
    }

    public async Task InitializeAsync()
    {
        // Check if relay is reachable before doing any setup
        _relayAvailable = await IsRelayReachableAsync();
        if (!_relayAvailable)
        {
            _logger.LogWarning("Relay at {RelayUrl} is not reachable, tests will be skipped", RelayUrl);
            return;
        }

        _logger.LogInformation("Relay at {RelayUrl} is reachable, setting up test contexts", RelayUrl);

        // -- User A --
        _nostrServiceA = new NostrService();
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        _dbPathA = Path.Combine(Path.GetTempPath(), $"openchat_relaytest_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA);
        await _storageA.InitializeAsync();
        await _storageA.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyA,
            PrivateKeyHex = _privKeyA,
            Npub = "npub1testA",
            Nsec = "nsec1testA",
            DisplayName = "User A (Managed)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceA = new ManagedMlsService(_storageA);
        _messageServiceA = new MessageService(_storageA, _nostrServiceA, _mlsServiceA);
        await _messageServiceA.InitializeAsync();

        // -- User B --
        _nostrServiceB = new NostrService();
        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        _dbPathB = Path.Combine(Path.GetTempPath(), $"openchat_relaytest_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB);
        await _storageB.InitializeAsync();
        await _storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = _pubKeyB,
            PrivateKeyHex = _privKeyB,
            Npub = "npub1testB",
            Nsec = "nsec1testB",
            DisplayName = "User B (Managed)",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        _mlsServiceB = new ManagedMlsService(_storageB);
        _messageServiceB = new MessageService(_storageB, _nostrServiceB, _mlsServiceB);
        await _messageServiceB.InitializeAsync();

        // Connect both to the relay
        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);
        await Task.Delay(1000); // Let connections stabilize

        _logger.LogInformation("Both users connected to relay. A={PubKeyA}, B={PubKeyB}",
            _pubKeyA[..16], _pubKeyB[..16]);
    }

    public async Task DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        if (_nostrServiceA != null)
        {
            await _nostrServiceA.DisconnectAsync();
            (_nostrServiceA as IDisposable)?.Dispose();
        }
        if (_nostrServiceB != null)
        {
            await _nostrServiceB.DisconnectAsync();
            (_nostrServiceB as IDisposable)?.Dispose();
        }

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (_dbPathA != null) TryDeleteFile(_dbPathA);
        if (_dbPathB != null) TryDeleteFile(_dbPathB);
    }

    // ===================================================================
    // Test 1: Full group chat — create, invite, accept, exchange messages
    // ===================================================================

    [SkippableFact]
    public async Task FullGroupChat_CreateInviteAcceptAndExchangeMessages()
    {
        Skip.IfNot(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Phase 1: User B generates and publishes KeyPackage
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64, $"KeyPackage should be >= 64 bytes, got {keyPackageB.Data.Length}");
        _output.WriteLine($"User B generated KeyPackage: {keyPackageB.Data.Length} bytes");

        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        Assert.Equal(64, kpEventId.Length);
        _output.WriteLine($"User B published KeyPackage to relay: {kpEventId}");

        await Task.Delay(1000); // Let relay store it

        // Phase 2: User A creates a group
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Real Relay Test Group");
        Assert.NotNull(groupInfo.GroupId);
        _output.WriteLine($"User A created group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // Save chat record for User A
        var nostrGroupIdA = _mlsServiceA.GetNostrGroupId(groupInfo.GroupId);
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Real Relay Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupIdA,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // Phase 3: User A fetches User B's KeyPackage from relay
        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var fetchedKP = fetchedKPs[0];
        Assert.NotNull(fetchedKP.EventJson);
        _output.WriteLine($"User A fetched User B's KeyPackage: {fetchedKP.NostrEventId}");

        // Phase 4: User A adds User B to the group
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKP);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"User A added User B: welcome={welcome.WelcomeData.Length} bytes");

        // Update A's participant list
        chatA.ParticipantPublicKeys.Add(_pubKeyB);
        await _storageA.SaveChatAsync(chatA);

        // Phase 5: User B subscribes to welcomes, then User A publishes Welcome
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, kpEventId);
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"User A published Welcome: {welcomeEventId}");

        // Phase 6: User B receives Welcome via relay
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var pendingInvite = await inviteTcs.Task;
        Assert.NotNull(pendingInvite);
        Assert.Equal(_pubKeyA, pendingInvite.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received invite: {pendingInvite.Id}");

        // Phase 7: User B accepts invite
        var chatB = await _messageServiceB.AcceptInviteAsync(pendingInvite.Id);
        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);
        _output.WriteLine($"User B accepted invite, chat: {chatB.Id}");

        // Phase 8: Subscribe both to group messages
        var nostrGroupIdHexA = nostrGroupIdA != null
            ? Convert.ToHexString(nostrGroupIdA).ToLowerInvariant()
            : Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        var nostrGroupIdB = chatB.NostrGroupId ?? chatB.MlsGroupId;
        var nostrGroupIdHexB = Convert.ToHexString(nostrGroupIdB!).ToLowerInvariant();

        await _nostrServiceA.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexA });
        await _nostrServiceB.SubscribeToGroupMessagesAsync(new[] { nostrGroupIdHexB });
        await Task.Delay(500);

        // Phase 9: User A sends a message, User B receives it
        var messageBTcs = new TaskCompletionSource<Message>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var msgSubB = _messageServiceB.NewMessages
            .Where(m => m.Content == "Hello from A via relay!")
            .Take(1)
            .Subscribe(msg => messageBTcs.TrySetResult(msg));

        var sentByA = await _messageServiceA.SendMessageAsync(chatA.Id, "Hello from A via relay!");
        Assert.Equal(MessageStatus.Sent, sentByA.Status);
        _output.WriteLine($"User A sent message, event={sentByA.NostrEventId}");

        using var msgBCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        msgBCts.Token.Register(() => messageBTcs.TrySetCanceled());

        var receivedByB = await messageBTcs.Task;
        Assert.Equal("Hello from A via relay!", receivedByB.Content);
        Assert.Equal(_pubKeyA, receivedByB.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received message: \"{receivedByB.Content}\"");

        // Phase 10: User B sends a reply, User A receives it
        var messageATcs = new TaskCompletionSource<Message>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var msgSubA = _messageServiceA.NewMessages
            .Where(m => m.Content == "Reply from B via relay!")
            .Take(1)
            .Subscribe(msg => messageATcs.TrySetResult(msg));

        var sentByB = await _messageServiceB.SendMessageAsync(chatB.Id, "Reply from B via relay!");
        Assert.Equal(MessageStatus.Sent, sentByB.Status);
        _output.WriteLine($"User B sent reply, event={sentByB.NostrEventId}");

        using var msgACts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        msgACts.Token.Register(() => messageATcs.TrySetCanceled());

        var receivedByA = await messageATcs.Task;
        Assert.Equal("Reply from B via relay!", receivedByA.Content);
        Assert.Equal(_pubKeyB, receivedByA.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User A received reply: \"{receivedByA.Content}\"");

        _output.WriteLine("FULL GROUP CHAT TEST PASSED: bidirectional message exchange via real relay");
    }

    // ===================================================================
    // Test 2: Published event has correct Nostr group ID (h-tag)
    // ===================================================================

    [SkippableFact]
    public async Task PublishedEvent_HasCorrectNostrGroupId()
    {
        Skip.IfNot(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Phase 1: User B generates and publishes KeyPackage
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000);

        // Phase 2: User A creates group and adds User B
        var groupInfo = await _mlsServiceA.CreateGroupAsync("H-Tag Test Group");
        var nostrGroupId = _mlsServiceA.GetNostrGroupId(groupInfo.GroupId);
        Assert.NotNull(nostrGroupId);
        var nostrGroupIdHex = Convert.ToHexString(nostrGroupId!).ToLowerInvariant();
        var mlsGroupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        _output.WriteLine($"MLS group ID:   {mlsGroupIdHex[..16]}...");
        _output.WriteLine($"Nostr group ID: {nostrGroupIdHex[..16]}...");

        // Save chat for A
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "H-Tag Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // Fetch B's KeyPackage, add B to group
        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        chatA.ParticipantPublicKeys.Add(_pubKeyB);
        await _storageA.SaveChatAsync(chatA);

        // Phase 3: User A sends a message (publishes kind 445 via PublishRawEventJsonAsync)
        await _messageServiceA.SendMessageAsync(chatA.Id, "H-tag test message");
        _output.WriteLine("User A sent test message");

        await Task.Delay(1000); // Let relay process

        // Phase 4: Fetch event back from relay via FetchGroupHistoryAsync
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        var events = (await _nostrServiceA.FetchGroupHistoryAsync(nostrGroupIdHex, since, until, 10)).ToList();
        Assert.NotEmpty(events);
        _output.WriteLine($"Fetched {events.Count} event(s) from relay");

        // Phase 5: Verify the h-tag uses NostrGroupId, not MLS group ID
        var lastEvent = events.Last();
        var hTag = lastEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "h");
        Assert.NotNull(hTag);
        Assert.Equal(nostrGroupIdHex, hTag![1]);
        Assert.NotEqual(mlsGroupIdHex, hTag[1]);
        _output.WriteLine($"h-tag value: {hTag[1]} (matches NostrGroupId, not MlsGroupId)");

        // Phase 6: Verify content is base64 (not double-wrapped JSON)
        var content = lastEvent.Content;
        Assert.False(string.IsNullOrEmpty(content));
        // Content should be valid base64 (MIP-03 ChaCha20 ciphertext)
        var decoded = Convert.FromBase64String(content);
        Assert.True(decoded.Length > 0, "Content should decode from base64 to non-empty bytes");
        // First byte should NOT be '{' (not a JSON-wrapped event)
        Assert.NotEqual((byte)'{', decoded[0]);
        _output.WriteLine($"Content is valid base64 ({decoded.Length} bytes), not double-wrapped JSON");

        _output.WriteLine("H-TAG TEST PASSED: event uses NostrGroupId and base64 ciphertext");
    }

    // ===================================================================
    // Test 3: Welcome event not rejected by relay (catches timestamp bug)
    // ===================================================================

    [SkippableFact]
    public async Task WelcomeEvent_NotRejectedByRelay()
    {
        Skip.IfNot(_relayAvailable, "Relay not available at " + RelayUrl);

        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Phase 1: User B generates a KeyPackage
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        var kpEventId = await _nostrServiceB.PublishKeyPackageAsync(
            keyPackageB.Data, _privKeyB, keyPackageB.NostrTags);
        _output.WriteLine($"User B published KeyPackage: {kpEventId}");

        await Task.Delay(1000);

        // Phase 2: User A creates group and adds User B
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Welcome Timestamp Test");
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Welcome Timestamp Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        var fetchedKPs = (await _nostrServiceA.FetchKeyPackagesAsync(_pubKeyB)).ToList();
        Assert.NotEmpty(fetchedKPs);
        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, fetchedKPs[0]);
        _output.WriteLine($"User A added User B, welcome={welcome.WelcomeData.Length} bytes");

        // Phase 3: User A publishes Welcome to relay
        var welcomeEventId = await _nostrServiceA.PublishWelcomeAsync(
            welcome.WelcomeData, _pubKeyB, _privKeyA, kpEventId);

        // If we get here without exception, the relay accepted the event
        Assert.Equal(64, welcomeEventId.Length);
        _output.WriteLine($"Welcome event published successfully: {welcomeEventId}");

        // Phase 4: User B subscribes and retrieves the Welcome
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB, _privKeyB);
        await Task.Delay(500);

        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // The Welcome was already published, but subscriptions might deliver stored events.
        // If not received yet, we trigger a rescan from the relay.
        Task<PendingInvite> waitTask = inviteTcs.Task;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());

        PendingInvite? receivedInvite = null;
        try
        {
            receivedInvite = await waitTask;
        }
        catch (TaskCanceledException)
        {
            // If subscription didn't deliver it, try rescan
            _output.WriteLine("Welcome not received via subscription, trying rescan...");
            await _messageServiceB.RescanInvitesAsync();
            var invites = (await _messageServiceB.GetPendingInvitesAsync()).ToList();
            Assert.NotEmpty(invites);
            receivedInvite = invites[0];
        }

        Assert.NotNull(receivedInvite);
        Assert.Equal(_pubKeyA, receivedInvite!.SenderPublicKey, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"User B received Welcome invite: {receivedInvite.Id}");

        // Phase 5: User B can accept the invite (Welcome data is valid)
        var chatB = await _messageServiceB.AcceptInviteAsync(receivedInvite.Id);
        Assert.NotNull(chatB);
        Assert.NotNull(chatB.MlsGroupId);
        _output.WriteLine($"User B accepted invite, joined group: {chatB.Name}");

        _output.WriteLine("WELCOME TIMESTAMP TEST PASSED: event accepted by relay and processable by recipient");
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static async Task<bool> IsRelayReachableAsync()
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(RelayUrl), cts.Token);
            // Don't bother with clean close — just check that connect succeeds
            return ws.State == WebSocketState.Open;
        }
        catch
        {
            return false;
        }
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
            // Temp file will be cleaned up eventually
        }
    }
}
