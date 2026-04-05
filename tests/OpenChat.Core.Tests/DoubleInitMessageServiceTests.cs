using System.Reactive.Subjects;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// Regression tests for the double-init bug: when ActivateSession is called twice on
/// startup (due to external signer restoration triggering LoginViewModel.LoggedInUser),
/// two MessageService instances subscribe to the same NostrService.Events. The first
/// instance saves incoming messages to the DB, causing the second instance (which the
/// UI is bound to) to skip them as "already-processed". Real-time messages are lost
/// until app restart.
/// </summary>
public class DoubleInitMessageServiceTests
{
    private readonly ITestOutputHelper _output;

    public DoubleInitMessageServiceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Documents WHY the ShellViewModel._sessionActivated guard is critical:
    /// if two MessageService instances share the same NostrService, the first one
    /// saves events to DB before the second can process them, causing the second
    /// (UI-bound) instance to skip them as "already-processed".
    /// The ShellViewModel guard prevents this scenario from occurring in production.
    /// </summary>
    [Fact]
    public async Task TwoMessageServices_SharingNostrService_DemonstratesWhyGuardIsNeeded()
    {
        // Arrange: shared NostrService events subject (simulates the singleton NostrService)
        var eventsSubject = new Subject<NostrEventReceived>();
        var savedEventIds = new HashSet<string>();

        var nostrMock = new Mock<INostrService>();
        nostrMock.Setup(n => n.Events).Returns(eventsSubject);

        // Shared storage (both MessageService instances hit the same DB)
        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetCurrentUserAsync())
            .ReturnsAsync(new User
            {
                Id = "user1",
                PublicKeyHex = "e9b03d7d20c787cec2ff418c0b447d927ad8fde2aa538f29dc3dfccbb254ceb2",
                PrivateKeyHex = new string('0', 64),
                IsCurrentUser = true
            });

        // Thread-safe DB dedup simulation
        storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string eventId) => savedEventIds.Contains(eventId));
        storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>()))
            .Returns((Message m) =>
            {
                if (!string.IsNullOrEmpty(m.NostrEventId))
                    savedEventIds.Add(m.NostrEventId);
                return Task.CompletedTask;
            });

        var groupIdHex = "d93fdf1fee19573eb1997a79259af2d65ffd1138c8eb6aa22f2a7be9f1f7e682";
        var chat = new Chat
        {
            Id = "chat1",
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = Convert.FromHexString(groupIdHex),
            NostrGroupId = Convert.FromHexString(groupIdHex)
        };
        storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        storageMock.Setup(s => s.GetChatAsync("chat1")).ReturnsAsync(chat);
        storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);

        var decryptedMsg = new MlsDecryptedMessage
        {
            SenderPublicKey = "abcd" + new string('0', 60),
            Plaintext = "Hello from mobile!",
            Epoch = 1,
            RumorKind = 9
        };

        // Both MLS mocks return the same decrypted message
        var mlsMock1 = new Mock<IMlsService>();
        mlsMock1.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mlsMock1.Setup(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .ReturnsAsync(decryptedMsg);

        var mlsMock2 = new Mock<IMlsService>();
        mlsMock2.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mlsMock2.Setup(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .ReturnsAsync(decryptedMsg);

        // Create TWO MessageService instances sharing the same NostrService (the real bug)
        var messageService1 = new MessageService(storageMock.Object, nostrMock.Object, mlsMock1.Object);
        var messageService2 = new MessageService(storageMock.Object, nostrMock.Object, mlsMock2.Object);

        // Initialize both (simulates ActivateSession called twice)
        await messageService1.InitializeAsync();
        await messageService2.InitializeAsync();

        // UI binds to the SECOND MessageService (the one from the second ActivateSession)
        var receivedByService2 = new List<Message>();
        messageService2.NewMessages.Subscribe(m => receivedByService2.Add(m));

        // Also track service1 for diagnostic purposes
        var receivedByService1 = new List<Message>();
        messageService1.NewMessages.Subscribe(m => receivedByService1.Add(m));

        // Simulate an incoming kind 445 event from mobile
        var nostrEvent = new NostrEventReceived
        {
            Kind = 445,
            EventId = "38b8c8280a96786197f526cbd8d75e592ba4787a25edd5486ffd60c7894021fa",
            PublicKey = "6fc3da1a4dee4596" + new string('0', 48),
            Content = Convert.ToBase64String(new byte[100]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "h", groupIdHex }
            },
            RelayUrl = "wss://nos.lol/"
        };

        eventsSubject.OnNext(nostrEvent);

        // Give async handlers time to complete
        await Task.Delay(500);

        // Diagnostic output
        _output.WriteLine($"Messages received by service1: {receivedByService1.Count}");
        _output.WriteLine($"Messages received by service2: {receivedByService2.Count}");
        _output.WriteLine($"Event IDs in DB: {string.Join(", ", savedEventIds)}");

        // Assert: the second service (UI-bound) MISSES the message because the first
        // one saved it to DB first. This proves why ShellViewModel._sessionActivated guard
        // is critical — without it, real-time messages are lost.
        Assert.Equal(1, receivedByService1.Count);
        Assert.Equal(0, receivedByService2.Count);
    }

    /// <summary>
    /// Verifies the defense-in-depth fix: calling InitializeAsync twice on the same
    /// MessageService should dispose the previous subscription, ensuring only one
    /// event handler is active.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_CalledTwice_DisposePreviousSubscription()
    {
        var eventsSubject = new Subject<NostrEventReceived>();

        var nostrMock = new Mock<INostrService>();
        nostrMock.Setup(n => n.Events).Returns(eventsSubject);

        var storageMock = new Mock<IStorageService>();
        storageMock.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        storageMock.Setup(s => s.GetCurrentUserAsync())
            .ReturnsAsync(new User
            {
                Id = "user1",
                PublicKeyHex = "e9b03d7d20c787cec2ff418c0b447d927ad8fde2aa538f29dc3dfccbb254ceb2",
                PrivateKeyHex = new string('0', 64),
                IsCurrentUser = true
            });
        storageMock.Setup(s => s.MessageExistsByNostrEventIdAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
        storageMock.Setup(s => s.SaveMessageAsync(It.IsAny<Message>()))
            .Returns(Task.CompletedTask);

        var groupIdHex = "d93fdf1fee19573eb1997a79259af2d65ffd1138c8eb6aa22f2a7be9f1f7e682";
        var chat = new Chat
        {
            Id = "chat1",
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = Convert.FromHexString(groupIdHex),
            NostrGroupId = Convert.FromHexString(groupIdHex)
        };
        storageMock.Setup(s => s.GetAllChatsAsync()).ReturnsAsync(new List<Chat> { chat });
        storageMock.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);

        var mlsMock = new Mock<IMlsService>();
        mlsMock.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mlsMock.Setup(m => m.DecryptMessageAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .ReturnsAsync(new MlsDecryptedMessage
            {
                SenderPublicKey = "abcd" + new string('0', 60),
                Plaintext = "Hello!",
                Epoch = 1,
                RumorKind = 9
            });

        var messageService = new MessageService(storageMock.Object, nostrMock.Object, mlsMock.Object);

        // Initialize twice
        await messageService.InitializeAsync();
        await messageService.InitializeAsync();

        // Count how many times HandleGroupMessageEventAsync fires per event
        int handlerCallCount = 0;
        storageMock.Setup(s => s.GetAllChatsAsync())
            .Callback(() => handlerCallCount++)
            .ReturnsAsync(new List<Chat> { chat });

        eventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 445,
            EventId = "aabb" + new string('0', 60),
            PublicKey = "6fc3" + new string('0', 60),
            Content = Convert.ToBase64String(new byte[100]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "h", groupIdHex } },
            RelayUrl = "wss://nos.lol/"
        });

        await Task.Delay(500);

        _output.WriteLine($"Handler call count: {handlerCallCount}");

        // After the fix, InitializeAsync should dispose the previous subscription,
        // so only ONE handler fires per event
        Assert.Equal(1, handlerCallCount);
    }
}
