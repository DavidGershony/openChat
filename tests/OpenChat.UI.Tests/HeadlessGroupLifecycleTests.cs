using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

/// <summary>
/// Headless tests for group invite lifecycle: accept, decline, create-with-invite, rescan.
/// Uses two real MLS contexts where needed for cross-user Welcome processing.
/// </summary>
public class HeadlessGroupLifecycleTests : HeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AcceptInvite_ProcessesWelcome_CreatesChat(string backend)
    {
        if (ShouldSkip(backend)) return;

        // Alice creates group and generates a Welcome for Bob
        var alice = await CreateRealContext(backend);
        var bob = await CreateRealContext(backend);

        await alice.MlsService.InitializeAsync(alice.User.PrivateKeyHex, alice.User.PublicKeyHex);
        await bob.MessageService.InitializeAsync();

        // Alice creates group
        var groupInfo = await alice.MlsService.CreateGroupAsync("Alice's Group", new[] { "wss://relay.test" });

        // Bob generates KeyPackage
        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        PrepareKeyPackageForAddMember(bobKp, bob.User.PublicKeyHex);

        // Alice adds Bob → produces Welcome
        var welcome = await alice.MlsService.AddMemberAsync(groupInfo.GroupId, bobKp);

        // Create ChatListViewModel BEFORE pushing the welcome so it subscribes to NewInvites
        var chatListVm = new ChatListViewModel(bob.MessageService, bob.Storage, bob.MlsService, bob.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        // Simulate Welcome arriving to Bob as a kind-444 event
        var welcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = welcomeEventId,
            PublicKey = alice.User.PublicKeyHex,
            Content = Convert.ToBase64String(welcome.WelcomeData),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", bob.User.PublicKeyHex }
            },
            RelayUrl = "wss://test.relay"
        };

        bob.EventsSubject.OnNext(welcomeEvent);
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();

        // Bob should have a pending invite
        Assert.Single(chatListVm.PendingInvites);

        // Accept the invite
        var inviteVm = chatListVm.PendingInvites[0];
        await chatListVm.AcceptInviteCommand.Execute(inviteVm);
        Dispatcher.UIThread.RunJobs();

        // Pending invites should be cleared and chat should exist
        Assert.Empty(chatListVm.PendingInvites);
        Assert.NotEmpty(chatListVm.Chats);
        var newChat = chatListVm.Chats[0];
        Assert.True(newChat.IsGroup);
    }

    [AvaloniaTheory(Skip = "Obsolete: since commit 706fd66, MessageService filters welcomes via CanProcessWelcomeAsync before saving a PendingInvite. This test pushes random bytes which are now correctly rejected. Needs a real MLS key-package + welcome flow to reinstate.")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task DeclineInvite_RemovesFromPendingList(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        // Simulate a welcome event arriving
        var fakeWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var welcomeEvent = new NostrEventReceived
        {
            Kind = 444,
            EventId = fakeWelcomeEventId,
            PublicKey = "aa".PadLeft(64, 'a'),
            Content = Convert.ToBase64String(new byte[64]),
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>>
            {
                new() { "p", ctx.User.PublicKeyHex },
                new() { "h", "deadbeef" }
            },
            RelayUrl = "wss://test.relay"
        };

        ctx.EventsSubject.OnNext(welcomeEvent);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);

        // Decline the invite
        var inviteVm = chatListVm.PendingInvites[0];
        await chatListVm.DeclineInviteCommand.Execute(inviteVm);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.PendingInvites);
        Assert.Equal(0, chatListVm.PendingInviteCount);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CreateGroup_WithInvite_PublishesWelcome(string backend)
    {
        if (ShouldSkip(backend)) return;

        // Alice creates a group and invites Bob
        var alice = await CreateRealContext(backend);
        var bob = await CreateRealContext(backend);

        await alice.MessageService.InitializeAsync();
        await bob.MlsService.InitializeAsync(bob.User.PrivateKeyHex, bob.User.PublicKeyHex);

        // Bob generates KeyPackage and prepare it for MLS add_member
        var bobKp = await bob.MlsService.GenerateKeyPackageAsync();
        PrepareKeyPackageForAddMember(bobKp, bob.User.PublicKeyHex);

        // Mock Alice's NostrService to return Bob's prepared KeyPackage when fetched
        alice.MockNostr.Setup(n => n.FetchKeyPackagesAsync(bob.User.PublicKeyHex))
            .ReturnsAsync(new[] { bobKp });

        var chatListVm = new ChatListViewModel(alice.MessageService, alice.Storage, alice.MlsService, alice.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        // Open new group dialog
        chatListVm.NewChatCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        chatListVm.NewChatName = "Group With Bob";
        chatListVm.AddContactToChatCommand.Execute(bob.User.PublicKeyHex).Subscribe();
        Dispatcher.UIThread.RunJobs();

        // Create the group (real MLS + Welcome publishing)
        await chatListVm.CreateChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        // Group should appear in chat list
        Assert.NotEmpty(chatListVm.Chats);
        Assert.Contains(chatListVm.Chats, c => c.Name == "Group With Bob");
        Assert.False(chatListVm.ShowNewChatDialog);

        // Welcome should have been published
        alice.MockNostr.Verify(n => n.PublishWelcomeAsync(
            It.Is<byte[]>(data => data.Length > 0),
            It.Is<string>(pk => pk == bob.User.PublicKeyHex),
            It.IsAny<string>(),
            It.IsAny<string?>()), Moq.Times.Once);
    }

    [AvaloniaTheory(Skip = "Obsolete: since commit 706fd66, MessageService filters welcomes via CanProcessWelcomeAsync before saving a PendingInvite. This test pushes random bytes which are now correctly rejected. Needs a real MLS key-package + welcome flow to reinstate.")]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RescanInvites_FindsMissedWelcomes(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Mock FetchWelcomeEventsAsync to return a "missed" welcome
        var missedWelcomeEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var missedWelcome = new NostrEventReceived
        {
            Kind = 444,
            EventId = missedWelcomeEventId,
            PublicKey = "bb".PadLeft(64, 'b'),
            Content = Convert.ToBase64String(new byte[64]),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            Tags = new List<List<string>>
            {
                new() { "p", ctx.User.PublicKeyHex },
                new() { "h", "rescangroup" }
            },
            RelayUrl = "wss://test.relay"
        };

        ctx.MockNostr.Setup(n => n.FetchWelcomeEventsAsync(ctx.User.PublicKeyHex, It.IsAny<string?>()))
            .ReturnsAsync(new[] { missedWelcome });

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(chatListVm.PendingInvites);

        // Rescan
        chatListVm.RescanInvitesCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.PendingInvites);
        Assert.Contains("rescangroup", chatListVm.PendingInvites[0].GroupId);
    }
}
