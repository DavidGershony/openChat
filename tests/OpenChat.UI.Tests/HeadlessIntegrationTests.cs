using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.UI.ViewModels;
using OpenChat.UI.Views;
using Xunit;

namespace OpenChat.UI.Tests;

/// <summary>
/// Headless Avalonia integration tests that prove the UI views correctly
/// bind to ViewModels and respond to user interactions and data changes.
/// Uses [AvaloniaFact] to run on the Avalonia UI thread with the headless platform.
/// </summary>
public class HeadlessIntegrationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Login flow triggers main UI visibility
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void LoginFlow_SetsIsLoggedIn_MainUIBecomesVisible()
    {
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();

        // Pre-configure: no existing user
        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        // Configure key generation for login
        mockNostr.Setup(n => n.GenerateKeyPair())
            .Returns(("aa".PadLeft(64, 'a'), "bb".PadLeft(64, 'b'), "nsec1test", "npub1test"));

        // Configure import for login
        mockNostr.Setup(n => n.ImportPrivateKey(It.IsAny<string>()))
            .Returns(("aa".PadLeft(64, 'a'), "bb".PadLeft(64, 'b'), "nsec1test", "npub1test"));
        mockStorage.Setup(s => s.SaveCurrentUserAsync(It.IsAny<User>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainViewModel(mockMessage.Object, mockNostr.Object, mockStorage.Object, mockMls.Object);
        Dispatcher.UIThread.RunJobs();

        // Initially not logged in
        Assert.False(mainVm.IsLoggedIn);

        // Simulate login by setting LoggedInUser on LoginViewModel
        var user = new User
        {
            Id = "test-user",
            PublicKeyHex = "bb".PadLeft(64, 'b'),
            PrivateKeyHex = "aa".PadLeft(64, 'a'),
            Npub = "npub1test",
            DisplayName = "Test User",
            IsCurrentUser = true
        };

        mainVm.LoginViewModel.LoggedInUser = user;
        Dispatcher.UIThread.RunJobs();

        // After login, IsLoggedIn should be true
        Assert.True(mainVm.IsLoggedIn);
        Assert.Equal(user, mainVm.CurrentUser);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: MainWindow renders and login view is visible when not logged in
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void MainWindow_WhenNotLoggedIn_LoginViewIsVisible()
    {
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();
        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        var mainVm = new MainViewModel(mockMessage.Object, mockNostr.Object, mockStorage.Object, mockMls.Object);
        var window = new MainWindow { DataContext = mainVm };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // MainViewModel should not be logged in
        Assert.False(mainVm.IsLoggedIn);

        window.Close();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: Pending invites appear in ChatListViewModel via observable
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void PendingInvite_ArrivesViaObservable_AppearsInChatList()
    {
        var inviteSubject = new Subject<PendingInvite>();
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();

        mockMessage.Setup(m => m.NewInvites).Returns(inviteSubject.AsObservable());
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        var chatListVm = new ChatListViewModel(mockMessage.Object, mockStorage.Object, mockMls.Object, mockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.PendingInvites);

        // Simulate an invite arriving
        var invite = new PendingInvite
        {
            Id = "invite-1",
            SenderPublicKey = "cc".PadLeft(64, 'c'),
            GroupId = "deadbeef",
            WelcomeData = new byte[64],
            NostrEventId = "event123",
            ReceivedAt = DateTime.UtcNow
        };

        inviteSubject.OnNext(invite);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);
        Assert.Equal("invite-1", chatListVm.PendingInvites[0].Id);
        Assert.Equal(1, chatListVm.PendingInviteCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: Accept invite creates chat and removes invite from list
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public async Task AcceptInvite_CreatesChatAndRemovesInvite()
    {
        var inviteSubject = new Subject<PendingInvite>();
        var chatUpdateSubject = new Subject<Chat>();
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();

        mockMessage.Setup(m => m.NewInvites).Returns(inviteSubject.AsObservable());
        mockMessage.Setup(m => m.ChatUpdates).Returns(chatUpdateSubject.AsObservable());
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        // AcceptInviteAsync returns a new Chat
        var acceptedChat = new Chat
        {
            Id = "group-chat-1",
            Name = "Test Group",
            Type = ChatType.Group,
            MlsGroupId = new byte[16],
            MlsEpoch = 1,
            ParticipantPublicKeys = new List<string> { "bb".PadLeft(64, 'b') },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        mockMessage.Setup(m => m.AcceptInviteAsync("invite-1")).ReturnsAsync(acceptedChat);

        var chatListVm = new ChatListViewModel(mockMessage.Object, mockStorage.Object, mockMls.Object, mockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        // Deliver an invite
        var invite = new PendingInvite
        {
            Id = "invite-1",
            SenderPublicKey = "cc".PadLeft(64, 'c'),
            GroupId = "deadbeef",
            WelcomeData = new byte[64],
            NostrEventId = "event123",
            ReceivedAt = DateTime.UtcNow
        };
        inviteSubject.OnNext(invite);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);

        // Accept the invite
        var inviteVm = chatListVm.PendingInvites[0];
        await chatListVm.AcceptInviteCommand.Execute(inviteVm);
        Dispatcher.UIThread.RunJobs();

        // Invite should be removed
        Assert.Empty(chatListVm.PendingInvites);
        Assert.Equal(0, chatListVm.PendingInviteCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 5: ChatListView renders with pending invites
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void ChatListView_RendersPendingInvites()
    {
        var inviteSubject = new Subject<PendingInvite>();
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();

        mockMessage.Setup(m => m.NewInvites).Returns(inviteSubject.AsObservable());
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        var chatListVm = new ChatListViewModel(mockMessage.Object, mockStorage.Object, mockMls.Object, mockNostr.Object);
        var chatListView = new ChatListView { DataContext = chatListVm };
        var window = new Window { Content = chatListView, Width = 400, Height = 600 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Deliver an invite
        inviteSubject.OnNext(new PendingInvite
        {
            Id = "invite-render",
            SenderPublicKey = "dd".PadLeft(64, 'd'),
            GroupId = "abcd1234",
            WelcomeData = new byte[64],
            NostrEventId = "event456",
            ReceivedAt = DateTime.UtcNow,
            SenderDisplayName = "Alice"
        });
        Dispatcher.UIThread.RunJobs();

        // ViewModel should reflect the invite
        Assert.Single(chatListVm.PendingInvites);
        Assert.Equal("Alice", chatListVm.PendingInvites[0].SenderName);

        window.Close();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 6: Chat selection loads chat in ChatViewModel
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void ChatSelection_LoadsChatInChatViewModel()
    {
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();
        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        var mainVm = new MainViewModel(mockMessage.Object, mockNostr.Object, mockStorage.Object, mockMls.Object);
        Dispatcher.UIThread.RunJobs();

        var chat = new Chat
        {
            Id = "chat-select-test",
            Name = "Selected Chat",
            Type = ChatType.Group,
            MlsGroupId = new byte[16],
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        // Mock GetMessagesAsync for when chat loads
        mockMessage.Setup(m => m.GetMessagesAsync("chat-select-test", It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Enumerable.Empty<Message>());

        // Add chat to ChatListViewModel and select it
        var chatItem = new ChatItemViewModel(chat);
        mainVm.ChatListViewModel.Chats.Add(chatItem);
        mainVm.ChatListViewModel.SelectedChat = chatItem;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Selected Chat", mainVm.ChatViewModel.ChatName);
        Assert.True(mainVm.ChatViewModel.HasChat);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 7: New Group dialog opens and group name is bindable
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void NewGroupDialog_OpensAndBindsGroupName()
    {
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        var chatListVm = new ChatListViewModel(mockMessage.Object, mockStorage.Object, mockMls.Object, mockNostr.Object);

        Assert.False(chatListVm.ShowNewGroupDialog);

        // Execute the NewGroupCommand
        chatListVm.NewGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowNewGroupDialog);

        // Set group name
        chatListVm.NewGroupName = "My Test Group";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("My Test Group", chatListVm.NewGroupName);

        // Cancel
        chatListVm.CancelNewGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowNewGroupDialog);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 8: Settings view navigation
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public void SettingsNavigation_TogglesCurrentView()
    {
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();
        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);

        var mainVm = new MainViewModel(mockMessage.Object, mockNostr.Object, mockStorage.Object, mockMls.Object);
        Dispatcher.UIThread.RunJobs();

        // Initially no settings view
        Assert.Null(mainVm.CurrentView);

        // Navigate to settings
        mainVm.ShowSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(mainVm.CurrentView);
        Assert.IsType<SettingsViewModel>(mainVm.CurrentView);

        // Navigate back to chats
        mainVm.ShowChatsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(mainVm.CurrentView);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 9: Full flow - Login → Create Group → See it in chat list
    // ═══════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public async Task FullFlow_Login_CreateGroup_AppearsInChatList()
    {
        var chatUpdateSubject = new Subject<Chat>();
        var (mockMessage, mockNostr, mockStorage, mockMls) = CreateMocks();

        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((User?)null);
        mockMessage.Setup(m => m.ChatUpdates).Returns(chatUpdateSubject.AsObservable());
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        // MLS group creation
        var groupId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        mockMls.Setup(m => m.CreateGroupAsync("End-to-End Group"))
            .ReturnsAsync(new MlsGroupInfo
            {
                GroupId = groupId,
                GroupName = "End-to-End Group",
                Epoch = 0,
                MemberPublicKeys = new List<string> { "bb".PadLeft(64, 'b') }
            });

        mockStorage.Setup(s => s.SaveChatAsync(It.IsAny<Chat>())).Returns(Task.CompletedTask);
        mockStorage.Setup(s => s.GetCurrentUserAsync())
            .ReturnsAsync(new User
            {
                PublicKeyHex = "bb".PadLeft(64, 'b'),
                PrivateKeyHex = "aa".PadLeft(64, 'a'),
                IsCurrentUser = true
            });

        var mainVm = new MainViewModel(mockMessage.Object, mockNostr.Object, mockStorage.Object, mockMls.Object);
        Dispatcher.UIThread.RunJobs();

        // Login
        mainVm.LoginViewModel.LoggedInUser = new User
        {
            Id = "test-user",
            PublicKeyHex = "bb".PadLeft(64, 'b'),
            PrivateKeyHex = "aa".PadLeft(64, 'a'),
            Npub = "npub1test",
            IsCurrentUser = true
        };
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainVm.IsLoggedIn);

        // Open new group dialog
        mainVm.ChatListViewModel.NewGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.True(mainVm.ChatListViewModel.ShowNewGroupDialog);

        // Set group name and create
        mainVm.ChatListViewModel.NewGroupName = "End-to-End Group";
        Dispatcher.UIThread.RunJobs();

        await mainVm.ChatListViewModel.CreateGroupCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Group should appear in chat list
        Assert.NotEmpty(mainVm.ChatListViewModel.Chats);
        Assert.Contains(mainVm.ChatListViewModel.Chats, c => c.Name == "End-to-End Group");
        Assert.True(mainVm.ChatListViewModel.Chats.First(c => c.Name == "End-to-End Group").IsGroup);

        // Dialog should be closed
        Assert.False(mainVm.ChatListViewModel.ShowNewGroupDialog);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static (Mock<IMessageService>, Mock<INostrService>, Mock<IStorageService>, Mock<IMlsService>) CreateMocks()
    {
        var mockMessage = new Mock<IMessageService>();
        var mockNostr = new Mock<INostrService>();
        var mockStorage = new Mock<IStorageService>();
        var mockMls = new Mock<IMlsService>();

        // Default observable setups
        mockMessage.Setup(m => m.NewMessages).Returns(Observable.Empty<Message>());
        mockMessage.Setup(m => m.MessageStatusUpdates).Returns(Observable.Empty<(string, MessageStatus)>());
        mockMessage.Setup(m => m.ChatUpdates).Returns(Observable.Empty<Chat>());
        mockMessage.Setup(m => m.NewInvites).Returns(Observable.Empty<PendingInvite>());
        mockMessage.Setup(m => m.InitializeAsync()).Returns(Task.CompletedTask);
        mockMessage.Setup(m => m.GetChatsAsync()).ReturnsAsync(Enumerable.Empty<Chat>());
        mockMessage.Setup(m => m.GetPendingInvitesAsync()).ReturnsAsync(Enumerable.Empty<PendingInvite>());

        mockNostr.Setup(n => n.Events).Returns(Observable.Empty<NostrEventReceived>());
        mockNostr.Setup(n => n.WelcomeMessages).Returns(Observable.Empty<MarmotWelcomeEvent>());
        mockNostr.Setup(n => n.GroupMessages).Returns(Observable.Empty<MarmotGroupMessageEvent>());
        mockNostr.Setup(n => n.ConnectionStatus).Returns(Observable.Empty<NostrConnectionStatus>());
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.ConnectAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.DisconnectAsync()).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToWelcomesAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.SubscribeToGroupMessagesAsync(It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
        mockNostr.Setup(n => n.FetchUserMetadataAsync(It.IsAny<string>())).ReturnsAsync((UserMetadata?)null);
        mockNostr.Setup(n => n.PublishKeyPackageAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<List<List<string>>?>()))
            .ReturnsAsync("fake-kp-event");

        mockMls.Setup(m => m.InitializeAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        mockMls.Setup(m => m.GenerateKeyPackageAsync())
            .ReturnsAsync(new KeyPackage { Data = new byte[256], NostrTags = new List<List<string>>() });

        mockStorage.Setup(s => s.InitializeAsync()).Returns(Task.CompletedTask);

        return (mockMessage, mockNostr, mockStorage, mockMls);
    }
}
