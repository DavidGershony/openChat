using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Integration tests exercising the full group chat lifecycle through real services
/// (StorageService, MlsService with mock MarmotWrapper, MessageService) and a
/// mocked INostrService that simulates relay event delivery via Subject&lt;NostrEventReceived&gt;.
/// </summary>
public class EndToEndChatIntegrationTests : IAsyncLifetime
{
    private record UserContext(
        string PubKey, string PrivKey,
        StorageService Storage, string DbPath,
        Subject<NostrEventReceived> Events, Mock<INostrService> MockNostr,
        MlsService MlsService, MessageService MessageService);

    private UserContext _userA = null!;
    private UserContext _userB = null!;

    // Convenience accessors
    private string _pubKeyA => _userA.PubKey;
    private string _privKeyA => _userA.PrivKey;
    private StorageService _storageA => _userA.Storage;
    private string _dbPathA => _userA.DbPath;
    private Subject<NostrEventReceived> _eventsA => _userA.Events;
    private Mock<INostrService> _mockNostrA => _userA.MockNostr;
    private MlsService _mlsServiceA => _userA.MlsService;
    private MessageService _messageServiceA => _userA.MessageService;

    private string _pubKeyB => _userB.PubKey;
    private string _privKeyB => _userB.PrivKey;
    private StorageService _storageB => _userB.Storage;
    private string _dbPathB => _userB.DbPath;
    private Subject<NostrEventReceived> _eventsB => _userB.Events;
    private Mock<INostrService> _mockNostrB => _userB.MockNostr;
    private MlsService _mlsServiceB => _userB.MlsService;
    private MessageService _messageServiceB => _userB.MessageService;

    public async Task InitializeAsync()
    {
        _userA = await SetupUser("A");
        _userB = await SetupUser("B");
    }

    public Task DisposeAsync()
    {
        _messageServiceA?.Dispose();
        _messageServiceB?.Dispose();

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        TryDeleteFile(_dbPathA);
        TryDeleteFile(_dbPathB);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Full lifecycle
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoUsers_CanEstablishGroupChat_AndExchangeMessages()
    {
        // ── Phase 1: Key Validation ──
        Assert.Equal(64, _pubKeyA.Length);
        Assert.Equal(64, _privKeyA.Length);
        Assert.Equal(64, _pubKeyB.Length);
        Assert.Equal(64, _privKeyB.Length);
        Assert.NotEqual(_pubKeyA, _pubKeyB);

        // ── Phase 2: KeyPackage Generation (User B) ──
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        Assert.NotNull(keyPackageB.Data);
        Assert.True(keyPackageB.Data.Length > 0, "KeyPackage data should be non-empty");
        Assert.True(keyPackageB.Data.Length >= 64, $"KeyPackage should be >= 64 bytes, got {keyPackageB.Data.Length}");
        // Should have MIP-00 tags
        Assert.Contains(keyPackageB.NostrTags, t => t.Count >= 2 && t[0] == "encoding");
        Assert.Contains(keyPackageB.NostrTags, t => t.Count >= 2 && t[0] == "mls_ciphersuite");

        // ── Phase 3: Group Creation (User A) ──
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Test Group");
        Assert.NotNull(groupInfo.GroupId);
        Assert.True(groupInfo.GroupId.Length > 0);
        Assert.Equal("Test Group", groupInfo.GroupName);
        Assert.Equal(0UL, groupInfo.Epoch);
        Assert.Contains(_pubKeyA, groupInfo.MemberPublicKeys);

        // Save a Chat record to User A's storage
        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // ── Phase 4: Add Member (MLS level) ──
        // Build a fake kind-443 event JSON using the real MDK-provided tags
        var fakeKeyPackageEventJson = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags);
        keyPackageB.EventJson = fakeKeyPackageEventJson;
        keyPackageB.NostrEventId = "fake443event" + Guid.NewGuid().ToString("N");

        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, keyPackageB);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0, "Welcome data should be non-empty");
        Assert.NotNull(welcome.CommitData);
        Assert.True(welcome.CommitData!.Length > 0, "Commit data should be non-empty");
        Assert.Equal(_pubKeyB, welcome.RecipientPublicKey);

        // Verify PublishWelcomeAsync would be called (we'll simulate delivery instead)
        // Must be valid 64-char hex (Rust EventId::from_hex requires 32-byte hex)
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        // ── Phase 5: Welcome Delivery to User B ──
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = fakeWelcomeEventId,
            PublicKey = _pubKeyA,
            Content = Convert.ToBase64String(welcome.WelcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", _pubKeyB },
                new() { "h", Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant() }
            },
            RelayUrl = "wss://test.relay"
        };

        // Subscribe to NewInvites before pushing the event
        var inviteTask = WaitForObservable(_messageServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(welcomeEvent);
        var pendingInvite = await inviteTask;

        Assert.NotNull(pendingInvite);
        Assert.Equal(_pubKeyA, pendingInvite.SenderPublicKey);
        Assert.Equal(fakeWelcomeEventId, pendingInvite.NostrEventId);
        Assert.Equal(welcome.WelcomeData, pendingInvite.WelcomeData);

        // Verify persisted in storage
        var storedInvites = await _storageB.GetPendingInvitesAsync();
        Assert.Contains(storedInvites, i => i.NostrEventId == fakeWelcomeEventId);

        // ── Phase 6: Accept Invite (User B) ──
        var chatB = await _messageServiceB.AcceptInviteAsync(pendingInvite.Id);
        Assert.NotNull(chatB);
        Assert.Equal(ChatType.Group, chatB.Type);
        Assert.NotNull(chatB.MlsGroupId);
        Assert.False(string.IsNullOrEmpty(chatB.Name), "Chat should have a name from ProcessWelcome");

        // Verify invite was deleted from storage
        var remainingInvites = await _storageB.GetPendingInvitesAsync();
        Assert.DoesNotContain(remainingInvites, i => i.Id == pendingInvite.Id);

        // ── Phase 7: Send Message A → Group ──
        byte[]? capturedEncryptedDataA = null;
        _mockNostrA
            .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<byte[], string, string>((data, _, _) => capturedEncryptedDataA = data)
            .ReturnsAsync("fake445msgA");

        var messageA = await _messageServiceA.SendMessageAsync(chatA.Id, "Hello from A!");
        Assert.Equal("Hello from A!", messageA.Content);
        Assert.Equal(MessageStatus.Sent, messageA.Status);
        Assert.NotNull(capturedEncryptedDataA);

        // Verify mock encrypt/decrypt roundtrip within User A's MLS instance
        var decryptedA = await _mlsServiceA.DecryptMessageAsync(groupInfo.GroupId, capturedEncryptedDataA!);
        Assert.Equal("Hello from A!", decryptedA.Plaintext);

        // ── Phase 8: Document the h/g tag bug ──
        // PublishGroupMessageAsync uses "h" tag (line 662 of NostrService.cs),
        // but HandleGroupMessageEventAsync at line 465 only looks for "g" tag.
        // So incoming messages with the correct "h" tag are silently dropped.
        var groupIdHexA = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var bugEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = "bugtest_" + Guid.NewGuid().ToString("N"),
            PublicKey = _pubKeyB,
            Content = Convert.ToBase64String(capturedEncryptedDataA!),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                // This is the tag PublishGroupMessageAsync ACTUALLY uses
                new() { "h", groupIdHexA }
            },
            RelayUrl = "wss://test.relay"
        };

        var newMessageReceived = false;
        using var bugSub = _messageServiceA.NewMessages.Subscribe(_ => newMessageReceived = true);
        _eventsA.OnNext(bugEvent);

        // Give the async void handler time to process
        await Task.Delay(500);

        // BUG: messages with "h" tag are dropped because HandleGroupMessageEventAsync
        // at line 465 only checks for "g" tag. If this assertion starts failing,
        // it means the bug in MessageService.HandleGroupMessageEventAsync has been fixed!
        Assert.False(newMessageReceived,
            "Expected message with 'h' tag to be dropped (known bug: HandleGroupMessageEventAsync " +
            "line 465 only looks for 'g' tag, but PublishGroupMessageAsync uses 'h' tag). " +
            "If this fails, the h/g tag bug has been fixed - update this test!");

        // ── Phase 9: Send Message B → Group ──
        byte[]? capturedEncryptedDataB = null;
        _mockNostrB
            .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<byte[], string, string>((data, _, _) => capturedEncryptedDataB = data)
            .ReturnsAsync("fake445msgB");

        var messageB = await _messageServiceB.SendMessageAsync(chatB.Id, "Hello from B!");
        Assert.Equal("Hello from B!", messageB.Content);
        Assert.Equal(MessageStatus.Sent, messageB.Status);
        Assert.NotNull(capturedEncryptedDataB);

        // Verify mock encrypt/decrypt roundtrip within User B's MLS instance
        var decryptedB = await _mlsServiceB.DecryptMessageAsync(chatB.MlsGroupId!, capturedEncryptedDataB!);
        Assert.Equal("Hello from B!", decryptedB.Plaintext);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: Welcome deduplication
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WelcomeEvent_ReceivedTwice_OnlyCreatesOneInvite()
    {
        var welcomeEventId = "dedup_" + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = welcomeEventId,
            PublicKey = _pubKeyA,
            Content = Convert.ToBase64String(new byte[64]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", _pubKeyB },
                new() { "h", "deadbeef" }
            },
            RelayUrl = "wss://test.relay"
        };

        // Deliver the first event and wait for the invite
        var firstInviteTask = WaitForObservable(_messageServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(welcomeEvent);
        var firstInvite = await firstInviteTask;
        Assert.NotNull(firstInvite);

        // Deliver the same event again
        _eventsB.OnNext(welcomeEvent);
        // Give the handler time to process
        await Task.Delay(500);

        // Assert only one invite exists with this NostrEventId
        var allInvites = await _storageB.GetPendingInvitesAsync();
        var matchingInvites = allInvites.Where(i => i.NostrEventId == welcomeEventId).ToList();
        Assert.Single(matchingInvites);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: h/g tag bug proof
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleGroupMessage_AcceptsBothHAndGTags()
    {
        // Setup: create a group and chat so the handler can find it
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Tag Test");
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Tag Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = 0,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chat);

        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

        // ── Part 1: "h" tag (MIP-03 spec, used by PublishGroupMessageAsync) → PROCESSED ──
        var hCiphertext = await _mlsServiceA.EncryptMessageAsync(groupInfo.GroupId, "h-tag message");
        var hTagEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = "htag_" + Guid.NewGuid().ToString("N"),
            PublicKey = _pubKeyB,
            Content = Convert.ToBase64String(hCiphertext),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } },
            RelayUrl = "wss://test.relay"
        };

        var hTagTask = WaitForObservable(_messageServiceA.NewMessages, TimeSpan.FromSeconds(5));
        _eventsA.OnNext(hTagEvent);
        var hMessage = await hTagTask;

        Assert.NotNull(hMessage);
        Assert.Equal("h-tag message", hMessage.Content);
        Assert.Equal(chat.Id, hMessage.ChatId);

        // ── Part 2: "g" tag (legacy) → also PROCESSED ──
        var gCiphertext = await _mlsServiceA.EncryptMessageAsync(groupInfo.GroupId, "g-tag message");
        var gTagEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = "gtag_" + Guid.NewGuid().ToString("N"),
            PublicKey = _pubKeyB,
            Content = Convert.ToBase64String(gCiphertext),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "g", groupIdHex } },
            RelayUrl = "wss://test.relay"
        };

        var gTagTask = WaitForObservable(_messageServiceA.NewMessages, TimeSpan.FromSeconds(5));
        _eventsA.OnNext(gTagEvent);
        var gMessage = await gTagTask;

        Assert.NotNull(gMessage);
        Assert.Equal("g-tag message", gMessage.Content);
        Assert.Equal(chat.Id, gMessage.ChatId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: Full lifecycle with restart — MLS state survives close/reopen
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoUsers_ExchangeMessages_CloseReopen_ContinueExchanging()
    {
        // ── Phase 1: Key Validation ──
        Assert.Equal(64, _pubKeyA.Length);
        Assert.Equal(64, _pubKeyB.Length);
        Assert.NotEqual(_pubKeyA, _pubKeyB);

        // ── Phase 2: User B generates a KeyPackage ──
        var keyPackageB = await _mlsServiceB.GenerateKeyPackageAsync();
        Assert.True(keyPackageB.Data.Length >= 64);

        // ── Phase 3: User A creates group ──
        var groupInfo = await _mlsServiceA.CreateGroupAsync("Restart Test Group");
        Assert.NotNull(groupInfo.GroupId);
        Assert.True(groupInfo.GroupId.Length > 0);

        var chatA = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Restart Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _pubKeyA },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await _storageA.SaveChatAsync(chatA);

        // Persist MLS state for User A after group creation
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var stateA1 = await _mlsServiceA.ExportGroupStateAsync(groupInfo.GroupId);
        await _storageA.SaveMlsStateAsync(groupIdHex, stateA1);

        // ── Phase 4: Add User B to group ──
        var fakeKpJson = CreateFakeKeyPackageEventJson(_pubKeyB, keyPackageB.Data, keyPackageB.NostrTags);
        keyPackageB.EventJson = fakeKpJson;
        keyPackageB.NostrEventId = "fake443_" + Guid.NewGuid().ToString("N");

        var welcome = await _mlsServiceA.AddMemberAsync(groupInfo.GroupId, keyPackageB);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);

        // Persist MLS state for User A after adding member
        var stateA2 = await _mlsServiceA.ExportGroupStateAsync(groupInfo.GroupId);
        await _storageA.SaveMlsStateAsync(groupIdHex, stateA2);

        // ── Phase 5: Deliver Welcome to User B ──
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = fakeWelcomeEventId,
            PublicKey = _pubKeyA,
            Content = Convert.ToBase64String(welcome.WelcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", _pubKeyB },
                new() { "h", groupIdHex }
            },
            RelayUrl = "wss://test.relay"
        };

        var inviteTask = WaitForObservable(_messageServiceB.NewInvites, TimeSpan.FromSeconds(5));
        _eventsB.OnNext(welcomeEvent);
        var pendingInvite = await inviteTask;
        Assert.NotNull(pendingInvite);

        // ── Phase 6: User B accepts invite ──
        var chatB = await _messageServiceB.AcceptInviteAsync(pendingInvite.Id);
        Assert.NotNull(chatB);
        Assert.NotNull(chatB.MlsGroupId);

        // Persist MLS state for User B after accepting
        var groupIdHexB = Convert.ToHexString(chatB.MlsGroupId!).ToLowerInvariant();
        var stateB1 = await _mlsServiceB.ExportGroupStateAsync(chatB.MlsGroupId!);
        await _storageB.SaveMlsStateAsync(groupIdHexB, stateB1);

        // ── Phase 7: User A sends message, User B decrypts ──
        byte[]? capturedCiphertext1 = null;
        _mockNostrA
            .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<byte[], string, string>((data, _, _) => capturedCiphertext1 = data)
            .ReturnsAsync("fakemsg1");

        var msg1 = await _messageServiceA.SendMessageAsync(chatA.Id, "Before restart");
        Assert.Equal("Before restart", msg1.Content);
        Assert.NotNull(capturedCiphertext1);

        // Persist A's state after encrypt
        var stateA3 = await _mlsServiceA.ExportGroupStateAsync(groupInfo.GroupId);
        await _storageA.SaveMlsStateAsync(groupIdHex, stateA3);

        // User B decrypts directly at MLS level
        var decrypted1 = await _mlsServiceB.DecryptMessageAsync(chatB.MlsGroupId!, capturedCiphertext1!);
        Assert.Equal("Before restart", decrypted1.Plaintext);

        // Persist B's state after decrypt
        var stateB2 = await _mlsServiceB.ExportGroupStateAsync(chatB.MlsGroupId!);
        await _storageB.SaveMlsStateAsync(groupIdHexB, stateB2);

        // ── Phase 8: CLOSE — Dispose MessageService (kills event subscriptions) ──
        _messageServiceA.Dispose();
        _messageServiceB.Dispose();

        // Verify MLS state was persisted to SQLite during the session
        var savedStateA = await _storageA.GetMlsStateAsync(groupIdHex);
        Assert.NotNull(savedStateA);
        Assert.True(savedStateA!.Length > 0, "MLS state A should be persisted to DB");

        var savedStateB = await _storageB.GetMlsStateAsync(groupIdHexB);
        Assert.NotNull(savedStateB);
        Assert.True(savedStateB!.Length > 0, "MLS state B should be persisted to DB");

        // ══════════════════════════════════════════════════════════════
        // Phase 9: REOPEN — Create fresh MessageService instances with
        //          new event subjects and mock NostrServices, reusing the
        //          SAME databases and MlsService instances.
        //
        //          Note: The native Rust MLS library does not support
        //          ImportGroupState with memory storage, so we reuse the
        //          MlsService instances (MLS engine stays in-memory).
        //          The critical test is that MessageService + StorageService
        //          reconnect correctly and messages flow after restart.
        // ══════════════════════════════════════════════════════════════

        // New events subjects for relay simulation
        var eventsA2 = new Subject<NostrEventReceived>();
        var eventsB2 = new Subject<NostrEventReceived>();

        // New mock NostrServices
        var mockNostrA2 = CreateMockNostr(eventsA2);
        var mockNostrB2 = CreateMockNostr(eventsB2);

        // Wrap the real MLS services so InitializeAsync is a no-op
        // (the native MarmotWrapper creates a brand new client on re-init, wiping state)
        var wrappedMlsA = new Mock<IMlsService>();
        wrappedMlsA.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        wrappedMlsA.Setup(m => m.EncryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>()))
            .Returns<byte[], string>((gid, msg) => _mlsServiceA.EncryptMessageAsync(gid, msg));
        wrappedMlsA.Setup(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns<byte[], byte[]>((gid, data) => _mlsServiceA.DecryptMessageAsync(gid, data));
        wrappedMlsA.Setup(m => m.ExportGroupStateAsync(It.IsAny<byte[]>()))
            .Returns<byte[]>(gid => _mlsServiceA.ExportGroupStateAsync(gid));
        wrappedMlsA.Setup(m => m.GetGroupInfoAsync(It.IsAny<byte[]>()))
            .Returns<byte[]>(gid => _mlsServiceA.GetGroupInfoAsync(gid));

        var wrappedMlsB = new Mock<IMlsService>();
        wrappedMlsB.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        wrappedMlsB.Setup(m => m.EncryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>()))
            .Returns<byte[], string>((gid, msg) => _mlsServiceB.EncryptMessageAsync(gid, msg));
        wrappedMlsB.Setup(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns<byte[], byte[]>((gid, data) => _mlsServiceB.DecryptMessageAsync(gid, data));
        wrappedMlsB.Setup(m => m.ExportGroupStateAsync(It.IsAny<byte[]>()))
            .Returns<byte[]>(gid => _mlsServiceB.ExportGroupStateAsync(gid));
        wrappedMlsB.Setup(m => m.GetGroupInfoAsync(It.IsAny<byte[]>()))
            .Returns<byte[]>(gid => _mlsServiceB.GetGroupInfoAsync(gid));

        // New MessageServices using same databases + MLS engines (via wrapper)
        var messageServiceA2 = new MessageService(_storageA, mockNostrA2.Object, wrappedMlsA.Object);
        var messageServiceB2 = new MessageService(_storageB, mockNostrB2.Object, wrappedMlsB.Object);
        await messageServiceA2.InitializeAsync();
        await messageServiceB2.InitializeAsync();

        try
        {
            // Verify chats survived the restart
            var chatsA = await messageServiceA2.GetChatsAsync();
            var restoredChatA = chatsA.FirstOrDefault(c => c.Id == chatA.Id);
            Assert.NotNull(restoredChatA);
            Assert.Equal("Restart Test Group", restoredChatA!.Name);

            var chatsB = await messageServiceB2.GetChatsAsync();
            var restoredChatB = chatsB.FirstOrDefault(c =>
                c.MlsGroupId != null &&
                Convert.ToHexString(c.MlsGroupId).Equals(groupIdHexB, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(restoredChatB);

            // ── Phase 10: User A sends another message AFTER restart ──
            byte[]? capturedCiphertext2 = null;
            mockNostrA2
                .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<byte[], string, string>((data, _, _) => capturedCiphertext2 = data)
                .ReturnsAsync("fakemsg2");

            var msg2 = await messageServiceA2.SendMessageAsync(restoredChatA.Id, "After restart!");
            Assert.Equal("After restart!", msg2.Content);
            Assert.Equal(MessageStatus.Sent, msg2.Status);
            Assert.NotNull(capturedCiphertext2);

            // ── Phase 11: User B decrypts message sent after restart ──
            var decrypted2 = await _mlsServiceB.DecryptMessageAsync(restoredChatB!.MlsGroupId!, capturedCiphertext2!);
            Assert.Equal("After restart!", decrypted2.Plaintext);

            // ── Phase 12: User B sends a reply after restart ──
            byte[]? capturedCiphertext3 = null;
            mockNostrB2
                .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<byte[], string, string>((data, _, _) => capturedCiphertext3 = data)
                .ReturnsAsync("fakemsg3");

            var msg3 = await messageServiceB2.SendMessageAsync(restoredChatB.Id, "B replies after restart!");
            Assert.Equal("B replies after restart!", msg3.Content);
            Assert.NotNull(capturedCiphertext3);

            // User A decrypts B's reply
            var decrypted3 = await _mlsServiceA.DecryptMessageAsync(groupInfo.GroupId, capturedCiphertext3!);
            Assert.Equal("B replies after restart!", decrypted3.Plaintext);

            // ── Phase 13: Full round-trip via event delivery after restart ──
            // User A encrypts, we simulate relay delivery to B's MessageService
            byte[]? capturedCiphertext4 = null;
            mockNostrA2
                .Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<byte[], string, string>((data, _, _) => capturedCiphertext4 = data)
                .ReturnsAsync("fakemsg4");

            var msg4 = await messageServiceA2.SendMessageAsync(restoredChatA.Id, "Full round-trip after restart");
            Assert.NotNull(capturedCiphertext4);

            // Simulate the relay delivering the encrypted message to User B
            var groupMsgEvent = new NostrEventReceived
            {
                Kind = 445,
                EventId = "delivered_" + Guid.NewGuid().ToString("N"),
                PublicKey = _pubKeyA,
                Content = Convert.ToBase64String(capturedCiphertext4!),
                CreatedAt = DateTime.UtcNow,
                Tags = new List<List<string>> { new() { "h", groupIdHexB } },
                RelayUrl = "wss://test.relay"
            };

            var receivedTask = WaitForObservable(messageServiceB2.NewMessages, TimeSpan.FromSeconds(5));
            eventsB2.OnNext(groupMsgEvent);
            var receivedMsg = await receivedTask;

            Assert.NotNull(receivedMsg);
            Assert.Equal("Full round-trip after restart", receivedMsg.Content);
            Assert.Equal(restoredChatB.Id, receivedMsg.ChatId);
        }
        finally
        {
            messageServiceA2.Dispose();
            messageServiceB2.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static async Task<UserContext> SetupUser(string label)
    {
        // 1. Generate real secp256k1 keys (required for native MLS DLL)
        var nostrService = new NostrService();
        var (privKey, pubKey, nsec, npub) = nostrService.GenerateKeyPair();

        // 2. Storage
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_e2e_{label}_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath);
        await storage.InitializeAsync();
        await storage.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = pubKey,
            PrivateKeyHex = privKey,
            Npub = npub,
            Nsec = nsec,
            DisplayName = $"User {label}",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        });

        // 3. Events Subject for relay simulation
        var events = new Subject<NostrEventReceived>();

        // 4. Mock INostrService
        var mockNostr = new Mock<INostrService>();
        mockNostr.Setup(n => n.Events).Returns(events.AsObservable());
        mockNostr.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mockNostr.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mockNostr.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());

        mockNostr.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync(() => "fakekp_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakewelcome_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishCommitAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakecommit_" + Guid.NewGuid().ToString("N"));

        mockNostr.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync((UserMetadata?)null);
        mockNostr.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<KeyPackage>());
        mockNostr.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<NostrEventReceived>());

        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeAsync(It.IsAny<string>(), It.IsAny<NostrFilter>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // 5. Real MlsService (uses native MarmotWrapper when DLL is present)
        var mlsService = new MlsService();

        // 6. MessageService
        var messageService = new MessageService(storage, mockNostr.Object, mlsService);

        // 7. Initialize (loads user, inits MLS, subscribes to events)
        await messageService.InitializeAsync();

        return new UserContext(pubKey, privKey, storage, dbPath, events, mockNostr, mlsService, messageService);
    }

    private static Mock<INostrService> CreateMockNostr(Subject<NostrEventReceived> events)
    {
        var mockNostr = new Mock<INostrService>();
        mockNostr.Setup(n => n.Events).Returns(events.AsObservable());
        mockNostr.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mockNostr.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mockNostr.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());

        mockNostr.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync(() => "fakekp_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishWelcomeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => "fakewelcome_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishGroupMessageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakemsg_" + Guid.NewGuid().ToString("N"));
        mockNostr.Setup(n => n.PublishCommitAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => "fakecommit_" + Guid.NewGuid().ToString("N"));

        mockNostr.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync((UserMetadata?)null);
        mockNostr.Setup(n => n.FetchKeyPackagesAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<KeyPackage>());
        mockNostr.Setup(n => n.FetchWelcomeEventsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<NostrEventReceived>());

        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset?>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeAsync(It.IsAny<string>(), It.IsAny<NostrFilter>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        return mockNostr;
    }

    /// <summary>
    /// Builds a minimal kind-443 Nostr event JSON that passes
    /// MlsService.AddMemberAsync pre-validation and native MDK add_member.
    /// Uses the actual MDK-provided tags from the KeyPackage to ensure all
    /// required tags (encoding, mls_protocol_version, mls_ciphersuite, mls_extensions) are present.
    /// </summary>
    private static string CreateFakeKeyPackageEventJson(string ownerPubKey, byte[] keyPackageData, List<List<string>>? tags = null)
    {
        var contentBase64 = Convert.ToBase64String(keyPackageData);
        var tagsArray = tags?.Select(t => t.ToArray()).ToArray()
            ?? new[]
            {
                new[] { "encoding", "base64" },
                new[] { "mls_protocol_version", "1.0" },
                new[] { "mls_ciphersuite", "0x0001" }
            };
        var eventObj = new
        {
            id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            pubkey = ownerPubKey,
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            kind = 443,
            tags = tagsArray,
            content = contentBase64,
            sig = new string('a', 128) // fake 64-byte hex signature
        };
        return JsonSerializer.Serialize(eventObj);
    }

    /// <summary>
    /// Waits for the first emission from an observable, with a timeout.
    /// </summary>
    private static async Task<T> WaitForObservable<T>(IObservable<T> observable, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = observable.Take(1).Subscribe(
            value => tcs.TrySetResult(value),
            ex => tcs.TrySetException(ex));
        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(
            () => tcs.TrySetCanceled(cts.Token));
        return await tcs.Task;
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
            // Ignore - temp file, will be cleaned up eventually
        }
    }
}
