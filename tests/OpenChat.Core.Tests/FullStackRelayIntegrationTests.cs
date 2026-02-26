using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// Full-stack integration tests using a real Docker relay (ws://localhost:7777),
/// real NostrService, real StorageService, real MlsService, and real MessageService.
/// These tests prove the complete invite delivery pipeline works end-to-end.
///
/// Requires: docker compose -f docker-compose.test.yml up -d
/// </summary>
[Trait("Category", "Relay")]
public class FullStackRelayIntegrationTests : IAsyncLifetime
{
    private const string RelayUrl = "ws://localhost:7777";

    private readonly ITestOutputHelper _output;

    // User A (sender/inviter)
    private NostrService _nostrServiceA = null!;
    private StorageService _storageA = null!;
    private MlsService _mlsServiceA = null!;
    private MessageService _messageServiceA = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _dbPathA = null!;

    // User B (receiver/invitee)
    private NostrService _nostrServiceB = null!;
    private StorageService _storageB = null!;
    private MlsService _mlsServiceB = null!;
    private MessageService _messageServiceB = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;
    private string _dbPathB = null!;

    public FullStackRelayIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // ── User A setup ──
        _nostrServiceA = new NostrService();
        var keysA = _nostrServiceA.GenerateKeyPair();
        _privKeyA = keysA.privateKeyHex;
        _pubKeyA = keysA.publicKeyHex;

        _dbPathA = Path.Combine(Path.GetTempPath(), $"openchat_fullstack_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA);
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

        _mlsServiceA = new MlsService();
        _messageServiceA = new MessageService(_storageA, _nostrServiceA, _mlsServiceA);

        // ── User B setup ──
        _nostrServiceB = new NostrService();
        var keysB = _nostrServiceB.GenerateKeyPair();
        _privKeyB = keysB.privateKeyHex;
        _pubKeyB = keysB.publicKeyHex;

        _dbPathB = Path.Combine(Path.GetTempPath(), $"openchat_fullstack_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB);
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

        _mlsServiceB = new MlsService();
        _messageServiceB = new MessageService(_storageB, _nostrServiceB, _mlsServiceB);

        // Initialize MessageService for both users (subscribes to Events, inits MLS)
        await _messageServiceA.InitializeAsync();
        await _messageServiceB.InitializeAsync();

        // Connect both to the relay
        await _nostrServiceA.ConnectAsync(RelayUrl);
        await _nostrServiceB.ConnectAsync(RelayUrl);

        await Task.Delay(1000); // Let connections stabilize
    }

    public async Task DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        await _nostrServiceA.DisconnectAsync();
        await _nostrServiceB.DisconnectAsync();
        (_nostrServiceA as IDisposable)?.Dispose();
        (_nostrServiceB as IDisposable)?.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        TryDeleteFile(_dbPathA);
        TryDeleteFile(_dbPathB);
    }

    /// <summary>
    /// The critical test: User A publishes a Welcome (kind 444) to the real relay,
    /// User B has subscribed to welcomes, and MessageService should create a PendingInvite.
    /// This tests the FULL pipeline: publish → relay → WebSocket → Events observable →
    /// MessageService.OnNostrEventReceived → HandleWelcomeEventAsync → PendingInvite.
    /// </summary>
    [Fact]
    public async Task FullStack_WelcomePublished_ArriveAsPendingInvite()
    {
        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: User B subscribes to welcomes via the real relay
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB);
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
        using var rawCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        rawCts.Token.Register(() => rawEventTcs.TrySetCanceled());

        var rawEvent = await rawEventTcs.Task;
        _output.WriteLine($"Raw event confirmed: kind={rawEvent.Kind}, pubkey={rawEvent.PublicKey[..16]}...");
        Assert.Equal(444, rawEvent.Kind);
        Assert.Equal(_pubKeyA, rawEvent.PublicKey);

        // Step 5: Verify the PendingInvite was created by MessageService
        using var inviteCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        inviteCts.Token.Register(() => inviteTcs.TrySetCanceled());

        var invite = await inviteTcs.Task;
        _output.WriteLine($"PendingInvite confirmed: sender={invite.SenderPublicKey[..16]}..., welcomeDataLen={invite.WelcomeData.Length}");

        Assert.Equal(_pubKeyA, invite.SenderPublicKey);
        Assert.Equal(eventId, invite.NostrEventId);
        Assert.Equal(welcomeData, invite.WelcomeData);

        // Step 6: Verify invite was persisted to storage
        var storedInvites = (await _storageB.GetPendingInvitesAsync()).ToList();
        _output.WriteLine($"Stored invites count: {storedInvites.Count}");
        Assert.Contains(storedInvites, i => i.NostrEventId == eventId);

        _output.WriteLine("FULL STACK TEST PASSED: Welcome → Relay → NostrService → MessageService → PendingInvite → Storage");
    }

    /// <summary>
    /// Test that RescanInvitesAsync (used by the Rescan button in UI) can find
    /// Welcome events that were published BEFORE the subscription was set up.
    /// </summary>
    [Fact]
    public async Task FullStack_RescanInvites_FindsHistoricalWelcomes()
    {
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
        Assert.Contains(storedInvites, i => i.NostrEventId == eventId);

        _output.WriteLine("RESCAN TEST PASSED: Historical Welcome found via FetchWelcomeEventsAsync");
    }

    /// <summary>
    /// Test the complete invite acceptance flow after receiving via real relay.
    /// </summary>
    [Fact]
    public async Task FullStack_AcceptInvite_CreatesGroupChat()
    {
        _output.WriteLine($"User A pubkey: {_pubKeyA}");
        _output.WriteLine($"User B pubkey: {_pubKeyB}");

        // Step 1: Subscribe User B to welcomes
        await _nostrServiceB.SubscribeToWelcomesAsync(_pubKeyB);
        await Task.Delay(500);

        // Step 2: Set up invite listener
        var inviteTcs = new TaskCompletionSource<PendingInvite>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var inviteSub = _messageServiceB.NewInvites
            .Take(1)
            .Subscribe(invite => inviteTcs.TrySetResult(invite));

        // Step 3: User A publishes Welcome
        var welcomeData = new byte[256];
        RandomNumberGenerator.Fill(welcomeData);
        var eventId = await _nostrServiceA.PublishWelcomeAsync(
            welcomeData, _pubKeyB, _privKeyA);
        _output.WriteLine($"Welcome published: {eventId}");

        // Step 4: Wait for invite
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => inviteTcs.TrySetCanceled());
        var invite = await inviteTcs.Task;
        _output.WriteLine($"Invite received: {invite.Id}");

        // Step 5: Accept the invite
        var chat = await _messageServiceB.AcceptInviteAsync(invite.Id);
        _output.WriteLine($"Chat created: id={chat.Id}, name={chat.Name}, type={chat.Type}");

        Assert.NotNull(chat);
        Assert.Equal(ChatType.Group, chat.Type);
        Assert.NotNull(chat.MlsGroupId);

        // Step 6: Verify invite was cleaned up
        var remaining = (await _storageB.GetPendingInvitesAsync()).ToList();
        Assert.DoesNotContain(remaining, i => i.Id == invite.Id);

        // Step 7: Verify chat was persisted
        var savedChat = await _storageB.GetChatAsync(chat.Id);
        Assert.NotNull(savedChat);
        Assert.Equal(ChatType.Group, savedChat!.Type);

        _output.WriteLine("ACCEPT INVITE TEST PASSED: Welcome → PendingInvite → AcceptInvite → Chat");
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
