using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class MessageServiceTests : IDisposable
{
    private readonly Mock<IStorageService> _storageMock;
    private readonly Mock<INostrService> _nostrMock;
    private readonly Mock<IMlsService> _mlsMock;
    private readonly Subject<NostrEventReceived> _eventsSubject;
    private readonly MessageService _sut;

    private readonly User _currentUser = new()
    {
        Id = "user-1",
        PublicKeyHex = "aa".PadLeft(64, 'a'),
        PrivateKeyHex = "bb".PadLeft(64, 'b'),
        Npub = "npub1test",
        DisplayName = "Test User",
        CreatedAt = DateTime.UtcNow
    };

    public MessageServiceTests()
    {
        _storageMock = new Mock<IStorageService>();
        _nostrMock = new Mock<INostrService>();
        _mlsMock = new Mock<IMlsService>();
        _eventsSubject = new Subject<NostrEventReceived>();

        _nostrMock.Setup(n => n.Events).Returns(_eventsSubject.AsObservable());
        _nostrMock.Setup(n => n.ConnectedRelayUrls).Returns(new List<string> { "wss://relay.example.com" });

        _storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(_currentUser);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);

        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _sut = new MessageService(_storageMock.Object, _nostrMock.Object, _mlsMock.Object);
    }

    public void Dispose()
    {
        _eventsSubject.Dispose();
        _sut.Dispose();
    }

    private async Task InitializeServiceAsync()
    {
        await _sut.InitializeAsync();
    }

    // ─── Message routing ─────────────────────────────────────────────

    [Fact]
    public async Task HandleGroupMessage_Kind445_DecryptsAndSavesMessage()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var senderPubKey = "cc".PadLeft(64, 'c');

        var chat = new Chat
        {
            Id = "chat-1",
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, senderPubKey }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(senderPubKey)).ReturnsAsync(new User
        {
            Id = "user-2", PublicKeyHex = senderPubKey, DisplayName = "Sender", Npub = "npub1sender"
        });

        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = senderPubKey,
                Plaintext = "Hello from group!",
                Epoch = 1,
                RumorKind = 9,
                RumorEventId = "rumor123"
            });

        var receivedMessages = new List<Message>();
        using var sub = _sut.NewMessages.Subscribe(m => receivedMessages.Add(m));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "event-445-1".PadLeft(64, '0'),
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0xAA, 0xBB }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        // Allow async event handler to complete
        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SaveMessageAsync(It.Is<Message>(m =>
            m.Content == "Hello from group!" &&
            m.SenderPublicKey == senderPubKey &&
            m.ChatId == "chat-1" &&
            m.Status == MessageStatus.Delivered &&
            m.NostrEventId == "event-445-1".PadLeft(64, '0') &&
            m.RumorEventId == "rumor123"
        )), Times.Once);

        Assert.Single(receivedMessages);
        Assert.Equal("Hello from group!", receivedMessages[0].Content);
    }

    [Fact]
    public async Task HandleGroupMessage_Kind445_ReactionKind7_UpdatesTargetMessage()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var senderPubKey = "cc".PadLeft(64, 'c');
        var targetEventId = "target-rumor-id".PadLeft(64, '0');

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, senderPubKey }
        };

        var targetMessage = new Message
        {
            Id = "msg-target", ChatId = "chat-1", Content = "Original",
            SenderPublicKey = _currentUser.PublicKeyHex,
            Reactions = new Dictionary<string, List<string>>()
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetMessageByNostrEventIdAsync(targetEventId)).ReturnsAsync(targetMessage);

        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = senderPubKey,
                Plaintext = "\ud83d\udc4d",
                Epoch = 1,
                RumorKind = 7,
                ReactionTargetEventId = targetEventId,
                ReactionEmoji = "\ud83d\udc4d"
            });

        var reactionUpdates = new List<(string MessageId, string Emoji, string Reactor)>();
        using var sub = _sut.ReactionUpdates.Subscribe(r => reactionUpdates.Add(r));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "event-reaction".PadLeft(64, '0'),
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0xCC }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert - target message should have the reaction added
        Assert.True(targetMessage.Reactions.ContainsKey("\ud83d\udc4d"));
        Assert.Contains(senderPubKey, targetMessage.Reactions["\ud83d\udc4d"]);
        Assert.Single(reactionUpdates);
        Assert.Equal("msg-target", reactionUpdates[0].MessageId);
    }

    [Fact]
    public async Task HandleWelcomeEvent_Kind444_SavesPendingInvite()
    {
        // Arrange
        await InitializeServiceAsync();

        var welcomeData = new byte[] { 0x01, 0x02 };
        var senderPubKey = "dd".PadLeft(64, 'd');
        var eventId = "welcome-event-1".PadLeft(64, '0');

        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite>());
        _storageMock.Setup(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>())).Returns(Task.CompletedTask);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(senderPubKey)).ReturnsAsync((UserMetadata?)null);

        var invites = new List<PendingInvite>();
        using var sub = _sut.NewInvites.Subscribe(i => invites.Add(i));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 444,
            EventId = eventId,
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(welcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "h", "abcdef1234" },
                new() { "e", "kp-event-id" }
            }
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SavePendingInviteAsync(It.Is<PendingInvite>(i =>
            i.SenderPublicKey == senderPubKey &&
            i.GroupId == "abcdef1234" &&
            i.KeyPackageEventId == "kp-event-id" &&
            i.NostrEventId == eventId
        )), Times.Once);

        Assert.Single(invites);
    }

    [Fact]
    public async Task HandleGroupMessage_DuplicateEventId_Ignored()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var eventId = "dup-event".PadLeft(64, '0');

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(eventId)).ReturnsAsync(true);

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = eventId,
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert - decrypt should never be called for duplicates
        _mlsMock.Verify(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task HandleGroupMessage_MissingGroupTag_LogsWarningNoSave()
    {
        // Arrange
        await InitializeServiceAsync();

        // Act - event with no h/g tag
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "no-group-tag".PadLeft(64, '0'),
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>()
        });

        await Task.Delay(200);

        // Assert
        _mlsMock.Verify(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
        _storageMock.Verify(s => s.SaveMessageAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task HandleGroupMessage_DecryptFailure_EmitsDecryptionError()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("MLS decrypt failed"));

        var errors = new List<MlsDecryptionError>();
        using var sub = _sut.DecryptionErrors.Subscribe(e => errors.Add(e));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "bad-decrypt".PadLeft(64, '0'),
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert
        Assert.Single(errors);
        Assert.Equal("chat-1", errors[0].ChatId);
        Assert.Equal("Test Group", errors[0].ChatName);
        Assert.Contains("MLS decrypt failed", errors[0].ErrorMessage);
    }

    [Fact]
    public async Task HandleGroupMessage_CommitMessage_NoUserMessageSaved()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage { IsCommit = true, SenderPublicKey = "ee".PadLeft(64, 'e') });

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "commit-event".PadLeft(64, '0'),
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert - no message saved for commits
        _storageMock.Verify(s => s.SaveMessageAsync(It.IsAny<Message>()), Times.Never);
    }

    // ─── Send flow ───────────────────────────────────────────────────

    [Fact]
    public async Task SendMessageAsync_GroupChat_EncryptsAndPublishes()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var chatId = "chat-1";
        var chat = new Chat
        {
            Id = chatId, Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        _storageMock.Setup(s => s.GetChatAsync(chatId)).ReturnsAsync(chat);
        _mlsMock.Setup(m => m.EncryptMessageAsync(groupId, "Hello!", null))
            .ReturnsAsync(new byte[] { 0xEE, 0xFF });
        _mlsMock.Setup(m => m.LastEncryptedRumorEventId).Returns("rumor-id-123");
        _nostrMock.Setup(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("published-event-id");

        var statusUpdates = new List<(string Id, MessageStatus Status)>();
        using var sub = _sut.MessageStatusUpdates.Subscribe(s => statusUpdates.Add(s));

        // Act
        var result = await _sut.SendMessageAsync(chatId, "Hello!");

        // Assert
        Assert.Equal(MessageStatus.Sent, result.Status);
        Assert.Equal("Hello!", result.Content);
        Assert.Equal(_currentUser.PublicKeyHex, result.SenderPublicKey);
        Assert.Equal("published-event-id", result.NostrEventId);
        Assert.Equal("rumor-id-123", result.RumorEventId);
        Assert.True(result.IsFromCurrentUser);

        // Saved twice: once Pending, once after status update
        _storageMock.Verify(s => s.SaveMessageAsync(It.IsAny<Message>()), Times.Exactly(2));
        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.Id == chatId)), Times.Once);

        Assert.Single(statusUpdates);
        Assert.Equal(MessageStatus.Sent, statusUpdates[0].Status);
    }

    [Fact]
    public async Task SendMessageAsync_MlsEncryptFails_StatusSetToFailed()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var chatId = "chat-1";
        var chat = new Chat
        {
            Id = chatId, Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        _storageMock.Setup(s => s.GetChatAsync(chatId)).ReturnsAsync(chat);
        _mlsMock.Setup(m => m.EncryptMessageAsync(groupId, "Hello!", null))
            .ThrowsAsync(new InvalidOperationException("Encrypt failed"));

        // Act
        var result = await _sut.SendMessageAsync(chatId, "Hello!");

        // Assert
        Assert.Equal(MessageStatus.Failed, result.Status);
    }

    [Fact]
    public async Task SendMessageAsync_SavesLocally_BeforePublishing()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var chatId = "chat-1";
        var chat = new Chat
        {
            Id = chatId, Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex }
        };

        var savedStatuses = new List<MessageStatus>();
        _storageMock.Setup(s => s.GetChatAsync(chatId)).ReturnsAsync(chat);
        _storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>()))
            .Callback<Message>(m => savedStatuses.Add(m.Status))
            .Returns(Task.CompletedTask);
        _mlsMock.Setup(m => m.EncryptMessageAsync(groupId, "Hello!", null))
            .ReturnsAsync(new byte[] { 0xEE });
        _mlsMock.Setup(m => m.LastEncryptedRumorEventId).Returns((string?)null);
        _nostrMock.Setup(n => n.PublishRawEventJsonAsync(It.IsAny<byte[]>()))
            .ReturnsAsync("event-id");

        // Act
        await _sut.SendMessageAsync(chatId, "Hello!");

        // Assert - first save is Pending, second is Sent
        Assert.Equal(2, savedStatuses.Count);
        Assert.Equal(MessageStatus.Pending, savedStatuses[0]);
        Assert.Equal(MessageStatus.Sent, savedStatuses[1]);
    }

    [Fact]
    public async Task SendMessageAsync_BotChat_UsesGiftWrap()
    {
        // Arrange
        await InitializeServiceAsync();

        var botPubKey = "dd".PadLeft(64, 'd');
        var chatId = "bot-chat-1";
        var chat = new Chat
        {
            Id = chatId, Name = "Test Bot", Type = ChatType.Bot,
            MlsGroupId = null,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, botPubKey }
        };

        _storageMock.Setup(s => s.GetChatAsync(chatId)).ReturnsAsync(chat);
        _nostrMock.Setup(n => n.PublishGiftWrapAsync(
            14, "Hi bot!", It.IsAny<List<List<string>>>(),
            _currentUser.PrivateKeyHex, _currentUser.PublicKeyHex, botPubKey))
            .ReturnsAsync("giftwrap-event-id");

        // Act
        var result = await _sut.SendMessageAsync(chatId, "Hi bot!");

        // Assert
        Assert.Equal(MessageStatus.Sent, result.Status);
        Assert.Equal("giftwrap-event-id", result.NostrEventId);
        _nostrMock.Verify(n => n.PublishGiftWrapAsync(
            14, "Hi bot!", It.IsAny<List<List<string>>>(),
            _currentUser.PrivateKeyHex, _currentUser.PublicKeyHex, botPubKey), Times.Once);
    }

    [Fact]
    public async Task SendMessageAsync_NotLoggedIn_Throws()
    {
        // Arrange - don't initialize (no current user)
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);
        await _sut.InitializeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SendMessageAsync("chat-1", "Hello!"));
    }

    // ─── Chat lifecycle ──────────────────────────────────────────────

    [Fact]
    public async Task GetChatsAsync_ReturnsChatsBatchLoadingLastMessages()
    {
        // Arrange
        await InitializeServiceAsync();

        var chats = new List<Chat>
        {
            new() { Id = "chat-1", Name = "Chat 1", Type = ChatType.Group },
            new() { Id = "chat-2", Name = "Chat 2", Type = ChatType.Group }
        };
        var lastMessages = new Dictionary<string, Message>
        {
            ["chat-1"] = new() { Id = "msg-1", Content = "Latest" }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(chats);
        _storageMock.Setup(s => s.GetLastMessagePerChatAsync()).ReturnsAsync(lastMessages);

        // Act
        var result = (await _sut.GetChatsAsync()).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Latest", result[0].LastMessage?.Content);
        Assert.Null(result[1].LastMessage);
    }

    [Fact]
    public async Task AcceptInviteAsync_ProcessesWelcome_CreatesChatAndCleansUp()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x10, 0x20, 0x30 };
        var nostrGroupId = new byte[] { 0xAA, 0xBB };
        var inviteId = "invite-1";
        var welcomeData = new byte[] { 0x01, 0x02 };

        var invite = new PendingInvite
        {
            Id = inviteId,
            SenderPublicKey = "dd".PadLeft(64, 'd'),
            WelcomeData = welcomeData,
            NostrEventId = "welcome-event",
            KeyPackageEventId = "kp-event-1",
            SenderDisplayName = "Alice"
        };

        _storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite> { invite });
        _mlsMock.Setup(m => m.ProcessWelcomeAsync(welcomeData, "welcome-event"))
            .ReturnsAsync(new MlsGroupInfo
            {
                GroupId = groupId,
                GroupName = "Cool Group",
                Epoch = 0,
                MemberPublicKeys = new List<string> { _currentUser.PublicKeyHex, invite.SenderPublicKey }
            });
        _mlsMock.Setup(m => m.GetNostrGroupId(groupId)).Returns(nostrGroupId);
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storageMock.Setup(s => s.DismissWelcomeEventAsync("welcome-event")).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.DeletePendingInviteAsync(inviteId)).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.GetKeyPackageByNostrEventIdAsync("kp-event-1")).ReturnsAsync((KeyPackage?)null);

        // Act
        var chat = await _sut.AcceptInviteAsync(inviteId);

        // Assert
        Assert.Equal("Cool Group", chat.Name);
        Assert.Equal(ChatType.Group, chat.Type);
        Assert.Equal(groupId, chat.MlsGroupId);
        Assert.Equal(nostrGroupId, chat.NostrGroupId);

        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.Name == "Cool Group")), Times.Once);
        _storageMock.Verify(s => s.DismissWelcomeEventAsync("welcome-event"), Times.Once);
        _storageMock.Verify(s => s.DeletePendingInviteAsync(inviteId), Times.Once);
    }

    [Fact]
    public async Task AcceptInviteAsync_DuplicateGroup_ReturnsExistingChat()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x10, 0x20, 0x30 };
        var inviteId = "invite-dup";

        var invite = new PendingInvite
        {
            Id = inviteId,
            SenderPublicKey = "dd".PadLeft(64, 'd'),
            WelcomeData = new byte[] { 0x01 },
            NostrEventId = "welcome-dup"
        };

        var existingChat = new Chat
        {
            Id = "existing-chat", Name = "Existing Group", Type = ChatType.Group,
            MlsGroupId = groupId
        };

        _storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite> { invite });
        _mlsMock.Setup(m => m.ProcessWelcomeAsync(It.IsAny<byte[]>(), "welcome-dup"))
            .ReturnsAsync(new MlsGroupInfo { GroupId = groupId, GroupName = "Group" });
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { existingChat });
        _storageMock.Setup(s => s.DismissWelcomeEventAsync("welcome-dup")).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.DeletePendingInviteAsync(inviteId)).Returns(Task.CompletedTask);

        // Act
        var result = await _sut.AcceptInviteAsync(inviteId);

        // Assert - returns existing, no new chat saved
        Assert.Equal("existing-chat", result.Id);
        _storageMock.Verify(s => s.SaveChatAsync(It.IsAny<Chat>()), Times.Never);
    }

    [Fact]
    public async Task DeclineInviteAsync_DismissesAndDeletes()
    {
        // Arrange
        await InitializeServiceAsync();

        var invite = new PendingInvite
        {
            Id = "invite-decline",
            SenderPublicKey = "dd".PadLeft(64, 'd'),
            NostrEventId = "welcome-decline"
        };

        _storageMock.Setup(s => s.GetPendingInvitesAsync()).ReturnsAsync(new List<PendingInvite> { invite });
        _storageMock.Setup(s => s.DismissWelcomeEventAsync("welcome-decline")).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.DeletePendingInviteAsync("invite-decline")).Returns(Task.CompletedTask);

        // Act
        await _sut.DeclineInviteAsync("invite-decline");

        // Assert
        _storageMock.Verify(s => s.DismissWelcomeEventAsync("welcome-decline"), Times.Once);
        _storageMock.Verify(s => s.DeletePendingInviteAsync("invite-decline"), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateDirectMessageAsync_ExistingChat_ReturnsExisting()
    {
        // Arrange
        await InitializeServiceAsync();

        var recipientPubKey = "cc".PadLeft(64, 'c');
        var existingChat = new Chat
        {
            Id = "dm-existing", Name = "DM", Type = ChatType.DirectMessage,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, recipientPubKey }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { existingChat });

        // Act
        var result = await _sut.GetOrCreateDirectMessageAsync(recipientPubKey);

        // Assert
        Assert.Equal("dm-existing", result.Id);
        _storageMock.Verify(s => s.SaveChatAsync(It.IsAny<Chat>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateDirectMessageAsync_NewChat_CreatesAndSaves()
    {
        // Arrange
        await InitializeServiceAsync();

        var recipientPubKey = "cc".PadLeft(64, 'c');
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(recipientPubKey))
            .ReturnsAsync(new User { PublicKeyHex = recipientPubKey, DisplayName = "Bob", Npub = "npub1bob" });

        var chatUpdates = new List<Chat>();
        using var sub = _sut.ChatUpdates.Subscribe(c => chatUpdates.Add(c));

        // Act
        var result = await _sut.GetOrCreateDirectMessageAsync(recipientPubKey);

        // Assert
        Assert.Equal(ChatType.DirectMessage, result.Type);
        Assert.Equal("Bob", result.Name);
        Assert.Contains(recipientPubKey, result.ParticipantPublicKeys);
        Assert.Contains(_currentUser.PublicKeyHex, result.ParticipantPublicKeys);

        _storageMock.Verify(s => s.SaveChatAsync(It.IsAny<Chat>()), Times.Once);
        Assert.Single(chatUpdates);
    }

    // ─── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task HandleWelcomeEvent_DismissedEvent_Skipped()
    {
        // Arrange
        await InitializeServiceAsync();

        var eventId = "dismissed-welcome".PadLeft(64, '0');
        _storageMock.Setup(s => s.IsWelcomeEventDismissedAsync(eventId)).ReturnsAsync(true);

        var invites = new List<PendingInvite>();
        using var sub = _sut.NewInvites.Subscribe(i => invites.Add(i));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 444,
            EventId = eventId,
            PublicKey = "dd".PadLeft(64, 'd'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>()
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SavePendingInviteAsync(It.IsAny<PendingInvite>()), Times.Never);
        Assert.Empty(invites);
    }

    [Fact]
    public async Task HandleGroupMessage_NoMatchingChat_DoesNotSave()
    {
        // Arrange
        await InitializeServiceAsync();

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "orphan-event".PadLeft(64, '0'),
            PublicKey = "ee".PadLeft(64, 'e'),
            Content = Convert.ToBase64String(new byte[] { 0x01 }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", "deadbeef" } }
        });

        await Task.Delay(200);

        // Assert
        _mlsMock.Verify(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
        _storageMock.Verify(s => s.SaveMessageAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task HandleBotMessage_Kind14_SavesInBotChat()
    {
        // Arrange
        await InitializeServiceAsync();

        var botPubKey = "ff".PadLeft(64, 'f');
        var eventId = "bot-msg-event".PadLeft(64, '0');

        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(eventId)).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat>());
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(botPubKey)).ReturnsAsync((User?)null);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(botPubKey)).ReturnsAsync((UserMetadata?)null);

        var messages = new List<Message>();
        using var sub = _sut.NewMessages.Subscribe(m => messages.Add(m));

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 14,
            EventId = eventId,
            PublicKey = botPubKey,
            Content = "Bot says hello",
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>()
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SaveMessageAsync(It.Is<Message>(m =>
            m.Content == "Bot says hello" &&
            m.SenderPublicKey == botPubKey &&
            m.Status == MessageStatus.Delivered
        )), Times.Once);

        // Also saved the bot chat
        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.Type == ChatType.Bot)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleBotMessage_SelfEcho_Skipped()
    {
        // Arrange
        await InitializeServiceAsync();

        // Act - event from ourselves
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 14,
            EventId = "self-echo".PadLeft(64, '0'),
            PublicKey = _currentUser.PublicKeyHex,
            Content = "My own message echoed",
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>()
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SaveMessageAsync(It.IsAny<Message>()), Times.Never);
    }

    // ─── Chat operations ─────────────────────────────────────────────

    [Fact]
    public async Task ArchiveChatAsync_SetsArchivedAndEmitsUpdate()
    {
        // Arrange
        await InitializeServiceAsync();

        var chat = new Chat { Id = "chat-1", Name = "Test", IsArchived = false };
        _storageMock.Setup(s => s.GetChatAsync("chat-1")).ReturnsAsync(chat);

        var updates = new List<Chat>();
        using var sub = _sut.ChatUpdates.Subscribe(c => updates.Add(c));

        // Act
        await _sut.ArchiveChatAsync("chat-1");

        // Assert
        Assert.True(chat.IsArchived);
        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.IsArchived)), Times.Once);
        Assert.Single(updates);
    }

    [Fact]
    public async Task SetMutedAsync_UpdatesMutedStatus()
    {
        // Arrange
        await InitializeServiceAsync();

        var chat = new Chat { Id = "chat-1", Name = "Test", IsMuted = false };
        _storageMock.Setup(s => s.GetChatAsync("chat-1")).ReturnsAsync(chat);

        // Act
        await _sut.SetMutedAsync("chat-1", true);

        // Assert
        Assert.True(chat.IsMuted);
        _storageMock.Verify(s => s.SaveChatAsync(It.Is<Chat>(c => c.IsMuted)), Times.Once);
    }

    [Fact]
    public async Task LeaveGroupAsync_CleansUpLocalState()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var chat = new Chat
        {
            Id = "chat-leave", Name = "Leaving Group", Type = ChatType.Group,
            MlsGroupId = groupId
        };

        _storageMock.Setup(s => s.GetChatAsync("chat-leave")).ReturnsAsync(chat);
        _storageMock.Setup(s => s.DeleteMlsStateAsync(groupIdHex)).Returns(Task.CompletedTask);
        _storageMock.Setup(s => s.DeleteChatAsync("chat-leave")).Returns(Task.CompletedTask);

        // Act
        await _sut.LeaveGroupAsync("chat-leave");

        // Assert
        _storageMock.Verify(s => s.DeleteMlsStateAsync(groupIdHex), Times.Once);
        _storageMock.Verify(s => s.DeleteChatAsync("chat-leave"), Times.Once);
    }

    // ─── Initialization ──────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_SubscribesToNostrEvents()
    {
        // Arrange & Act
        await _sut.InitializeAsync();

        // Assert - MLS initialized with current user keys
        _mlsMock.Verify(m => m.InitializeAsync(_currentUser.PrivateKeyHex, _currentUser.PublicKeyHex), Times.Once);
        _storageMock.Verify(s => s.InitializeAsync(), Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_NoCurrentUser_SkipsMlsAndSubscription()
    {
        // Arrange
        _storageMock.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        // Act
        await _sut.InitializeAsync();

        // Assert
        _mlsMock.Verify(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_MlsFails_DoesNotThrow()
    {
        // Arrange
        _mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("MLS init exploded"));

        // Act & Assert - should not throw, MLS failure is logged but not fatal
        await _sut.InitializeAsync();
    }

    // ─── Media message type detection ────────────────────────────────

    [Fact]
    public async Task HandleGroupMessage_AudioImeta_CreatesAudioMessage()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var senderPubKey = "cc".PadLeft(64, 'c');

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, senderPubKey }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(senderPubKey)).ReturnsAsync((User?)null);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(senderPubKey)).ReturnsAsync((UserMetadata?)null);

        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = senderPubKey,
                Plaintext = "",
                Epoch = 1,
                RumorKind = 9,
                ImageUrl = "https://blossom.example/voice.ogg",
                MediaType = "audio/ogg",
                FileName = "voice.ogg"
            });

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "audio-event".PadLeft(64, '0'),
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0xAA }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SaveMessageAsync(It.Is<Message>(m =>
            m.Type == MessageType.Audio &&
            m.Content == "[Voice message]" &&
            m.ImageUrl == "https://blossom.example/voice.ogg"
        )), Times.Once);
    }

    [Fact]
    public async Task HandleGroupMessage_ImageImeta_CreatesImageMessage()
    {
        // Arrange
        await InitializeServiceAsync();

        var groupId = new byte[] { 0x01, 0x02, 0x03 };
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        var senderPubKey = "cc".PadLeft(64, 'c');

        var chat = new Chat
        {
            Id = "chat-1", Name = "Test Group", Type = ChatType.Group,
            MlsGroupId = groupId,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, senderPubKey }
        };

        _storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        _storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.GetUserByPublicKeyAsync(senderPubKey)).ReturnsAsync((User?)null);
        _nostrMock.Setup(n => n.FetchUserMetadataAsync(senderPubKey)).ReturnsAsync((UserMetadata?)null);

        _mlsMock.Setup(m => m.DecryptMessageAsync(groupId, It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = senderPubKey,
                Plaintext = "",
                Epoch = 1,
                RumorKind = 9,
                ImageUrl = "https://blossom.example/photo.jpg",
                MediaType = "image/jpeg"
            });

        // Act
        _eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "image-event".PadLeft(64, '0'),
            PublicKey = senderPubKey,
            Content = Convert.ToBase64String(new byte[] { 0xBB }),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } }
        });

        await Task.Delay(200);

        // Assert
        _storageMock.Verify(s => s.SaveMessageAsync(It.Is<Message>(m =>
            m.Type == MessageType.Image &&
            m.Content == "[Image]"
        )), Times.Once);
    }
}
