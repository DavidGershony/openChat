using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.UI.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

public class ViewModelTests
{
    [Fact]
    public void ChatItemViewModel_ShouldInitializeFromChat()
    {
        // Arrange
        var chat = new Chat
        {
            Id = "test-id",
            Name = "Test Chat",
            Type = ChatType.Group,
            UnreadCount = 5,
            IsPinned = true,
            IsMuted = false,
            LastActivityAt = DateTime.UtcNow,
            LastMessage = new Message { Content = "Last message" }
        };

        // Act
        var viewModel = new ChatItemViewModel(chat);

        // Assert
        Assert.Equal("test-id", viewModel.Id);
        Assert.Equal("Test Chat", viewModel.Name);
        Assert.True(viewModel.IsGroup);
        Assert.Equal(5, viewModel.UnreadCount);
        Assert.True(viewModel.IsPinned);
        Assert.False(viewModel.IsMuted);
        Assert.Equal("Last message", viewModel.LastMessagePreview);
    }

    [Fact]
    public void ChatItemViewModel_Update_ShouldUpdateProperties()
    {
        // Arrange
        var chat = new Chat
        {
            Id = "test-id",
            Name = "Initial Name",
            UnreadCount = 0,
            LastActivityAt = DateTime.UtcNow
        };
        var viewModel = new ChatItemViewModel(chat);

        // Act
        chat.Name = "Updated Name";
        chat.UnreadCount = 3;
        chat.LastMessage = new Message { Content = "New message" };
        viewModel.Update(chat);

        // Assert
        Assert.Equal("Updated Name", viewModel.Name);
        Assert.Equal(3, viewModel.UnreadCount);
        Assert.Equal("New message", viewModel.LastMessagePreview);
    }

    [Fact]
    public void MessageViewModel_ShouldInitializeFromMessage()
    {
        // Arrange
        var message = new Message
        {
            Id = "msg-id",
            SenderPublicKey = "sender123",
            Content = "Hello, World!",
            Timestamp = DateTime.UtcNow,
            IsFromCurrentUser = true,
            Status = MessageStatus.Delivered,
            Sender = new User { DisplayName = "Test User" }
        };

        // Act
        var viewModel = new MessageViewModel(message);

        // Assert
        Assert.Equal("msg-id", viewModel.Id);
        Assert.Equal("Hello, World!", viewModel.Content);
        Assert.True(viewModel.IsFromCurrentUser);
        Assert.Equal(MessageStatus.Delivered, viewModel.Status);
        Assert.Equal("Test User", viewModel.SenderName);
    }

    [Fact]
    public void MessageViewModel_ShouldTruncateSenderPublicKey_WhenNoSender()
    {
        // Arrange
        var message = new Message
        {
            Id = "msg-id",
            SenderPublicKey = "abcdefghijklmnopqrstuvwxyz123456",
            Content = "Test",
            Timestamp = DateTime.UtcNow
        };

        // Act
        var viewModel = new MessageViewModel(message);

        // Assert
        Assert.Equal("abcdefghijkl...", viewModel.SenderName);
    }

    [Fact]
    public void LoginViewModel_GenerateNewKey_ShouldPopulateGeneratedKeys()
    {
        // Arrange
        var mockNostrService = new Mock<INostrService>();
        mockNostrService.Setup(x => x.GenerateKeyPair())
            .Returns(("privateHex", "publicHex", "nsec1test", "npub1test"));

        var mockStorageService = new Mock<IStorageService>();

        var viewModel = new LoginViewModel(mockNostrService.Object, mockStorageService.Object);

        // Act
        viewModel.GenerateNewKeyCommand.Execute().Subscribe();

        // Assert
        Assert.Equal("nsec1test", viewModel.GeneratedNsec);
        Assert.Equal("npub1test", viewModel.GeneratedNpub);
        Assert.True(viewModel.ShowGeneratedKeys);
    }

    [Fact]
    public void LoginViewModel_ImportKey_ShouldSaveUser()
    {
        // Arrange
        var mockNostrService = new Mock<INostrService>();
        mockNostrService.Setup(x => x.ImportPrivateKey(It.IsAny<string>()))
            .Returns(("privateHex", "publicHex", "nsec1imported", "npub1imported"));

        var mockStorageService = new Mock<IStorageService>();
        User? savedUser = null;
        mockStorageService.Setup(x => x.SaveCurrentUserAsync(It.IsAny<User>()))
            .Callback<User>(u => savedUser = u)
            .Returns(Task.CompletedTask);
        mockStorageService.Setup(x => x.InitializeAsync())
            .Returns(Task.CompletedTask);

        var viewModel = new LoginViewModel(mockNostrService.Object, mockStorageService.Object);
        viewModel.PrivateKeyInput = "nsec1test";

        // Act
        viewModel.ImportKeyCommand.Execute().Subscribe();

        // Wait a bit for async operation to complete
        Thread.Sleep(100);

        // Assert
        Assert.NotNull(savedUser);
        Assert.Equal("publicHex", savedUser.PublicKeyHex);
        Assert.True(savedUser.IsCurrentUser);
        Assert.NotNull(viewModel.LoggedInUser);
    }

    [Fact]
    public void RelayViewModel_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var viewModel = new RelayViewModel
        {
            Url = "wss://relay.test",
            IsConnected = true
        };

        // Assert
        Assert.Equal("wss://relay.test", viewModel.Url);
        Assert.True(viewModel.IsConnected);
        Assert.Null(viewModel.Error);
    }
}
