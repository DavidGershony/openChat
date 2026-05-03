using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Headless tests for Direct Message and Bot/Device chat creation.
/// </summary>
public class HeadlessDmAndBotTests : HeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CreateDirectMessage_CreatesNewDmChat(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var recipientPubKey = "aa".PadLeft(64, 'a');
        var chat = await ctx.MessageService.GetOrCreateDirectMessageAsync(recipientPubKey);

        Assert.NotNull(chat);
        Assert.Equal(ChatType.DirectMessage, chat.Type);
        Assert.Contains(ctx.User.PublicKeyHex, chat.ParticipantPublicKeys);
        Assert.Contains(recipientPubKey, chat.ParticipantPublicKeys);

        // Verify persisted
        var stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(stored);
        Assert.Equal(ChatType.DirectMessage, stored!.Type);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CreateDirectMessage_ReturnsSameForExistingRecipient(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var recipientPubKey = "bb".PadLeft(64, 'b');
        var chat1 = await ctx.MessageService.GetOrCreateDirectMessageAsync(recipientPubKey);
        var chat2 = await ctx.MessageService.GetOrCreateDirectMessageAsync(recipientPubKey);

        Assert.Equal(chat1.Id, chat2.Id);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CreateBotChat_CreatesBotTypeChat(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var botPubKey = "cc".PadLeft(64, 'c');
        var chat = await ctx.MessageService.GetOrCreateBotChatAsync(botPubKey);

        Assert.NotNull(chat);
        Assert.Equal(ChatType.Bot, chat.Type);
        Assert.Contains(ctx.User.PublicKeyHex, chat.ParticipantPublicKeys);
        Assert.Contains(botPubKey, chat.ParticipantPublicKeys);

        // Verify persisted
        var stored = await ctx.Storage.GetChatAsync(chat.Id);
        Assert.NotNull(stored);
        Assert.Equal(ChatType.Bot, stored!.Type);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task CreateBotChat_ReturnsSameForExistingBot(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var botPubKey = "dd".PadLeft(64, 'd');
        var chat1 = await ctx.MessageService.GetOrCreateBotChatAsync(botPubKey);
        var chat2 = await ctx.MessageService.GetOrCreateBotChatAsync(botPubKey);

        Assert.Equal(chat1.Id, chat2.Id);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task BotChat_AppearsInChatListWithCorrectType(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var botPubKey = "ee".PadLeft(64, 'e');
        var chat = await ctx.MessageService.GetOrCreateBotChatAsync(botPubKey);

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chatListVm.AgentChats);
        Assert.True(chatListVm.AgentChats[0].IsBot);
        Assert.False(chatListVm.AgentChats[0].IsGroup);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LinkDevice_ViaViewModel_CreatesBotChat(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var botPubKey = "ff".PadLeft(64, 'f');
        ctx.MockNostr.Setup(n => n.NpubToHex(It.IsAny<string>())).Returns(botPubKey);

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        // Open dialog and set input
        chatListVm.AddBotCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowAddBotDialog);

        chatListVm.BotNpub = botPubKey;
        // Select "Add relay manually" mode and provide a relay URL
        chatListVm.BotRelayModeNip65 = false;
        chatListVm.BotRelayModeManual = true;
        chatListVm.BotManualRelay = "wss://relay.test.com";
        await chatListVm.CreateBotChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowAddBotDialog);
        Assert.Single(chatListVm.Chats);
        Assert.True(chatListVm.Chats[0].IsBot);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task BotResponse_FromDifferentKey_RoutesToExistingChatViaRelayMatch(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // User registers a bot chat with key A using a specific relay
        var registeredKey = "aa".PadLeft(64, 'a');
        var botRelayUrl = "wss://dvm.relay.test";
        ctx.MockNostr.Setup(n => n.PublishGiftWrapAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<List<List<string>>>(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>()))
            .ReturnsAsync("fakewrap_" + Guid.NewGuid().ToString("N"));
        var botChat = await ctx.MessageService.GetOrCreateBotChatAsync(registeredKey, new List<string> { botRelayUrl });

        // Simulate user sending a message so LastMessage.IsFromCurrentUser = true
        await ctx.MessageService.SendMessageAsync(botChat.Id, "Hello DVM!");
        await Task.Delay(100);

        // DVM responds with a DIFFERENT key from the same relay
        var responseKey = "bb".PadLeft(64, 'b');
        ctx.EventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 14,
            EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PublicKey = responseKey,
            Content = "DVM reply here",
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "p", ctx.User.PublicKeyHex } },
            RelayUrl = botRelayUrl
        });
        await Task.Delay(500);

        // The response must have been placed in the ORIGINAL bot chat, not a new one
        var allChats = (await ctx.Storage.GetAllChatsAsync()).Where(c => c.Type == ChatType.Bot).ToList();
        Assert.Single(allChats);
        Assert.Equal(botChat.Id, allChats[0].Id);

        var messages = (await ctx.Storage.GetMessagesForChatAsync(botChat.Id)).ToList();
        Assert.Contains(messages, m => m.Content == "DVM reply here");

        // The response key must now be registered in the chat so future messages go to the same place
        Assert.Contains(responseKey, allChats[0].ParticipantPublicKeys);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task BotResponse_FromDifferentKey_RoutesToExistingChatViaTimeFallback(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // User registers a bot chat with NO relay configured (time-only fallback path)
        var registeredKey = "cc".PadLeft(64, 'c');
        ctx.MockNostr.Setup(n => n.PublishGiftWrapAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<List<List<string>>>(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>()))
            .ReturnsAsync("fakewrap_" + Guid.NewGuid().ToString("N"));
        var botChat = await ctx.MessageService.GetOrCreateBotChatAsync(registeredKey);

        // Simulate user sending a message
        await ctx.MessageService.SendMessageAsync(botChat.Id, "Hello DVM no relay!");
        await Task.Delay(100);

        // DVM responds with a different key from a relay we don't recognise
        var responseKey = "dd".PadLeft(64, 'd');
        ctx.EventsSubject.OnNext(new NostrEventReceived
        {
            Kind = 14,
            EventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PublicKey = responseKey,
            Content = "Time-fallback reply",
            CreatedAt = DateTime.UtcNow,
            Tags = new List<List<string>> { new() { "p", ctx.User.PublicKeyHex } },
            RelayUrl = "wss://some.other.relay"
        });
        await Task.Delay(500);

        var allChats = (await ctx.Storage.GetAllChatsAsync()).Where(c => c.Type == ChatType.Bot).ToList();
        Assert.Single(allChats);
        Assert.Equal(botChat.Id, allChats[0].Id);

        var messages = (await ctx.Storage.GetMessagesForChatAsync(botChat.Id)).ToList();
        Assert.Contains(messages, m => m.Content == "Time-fallback reply");

        Assert.Contains(responseKey, allChats[0].ParticipantPublicKeys);
    }

    // --- Join Group by ID ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task JoinGroup_OpensDialog(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowJoinGroupDialog);

        chatListVm.JoinGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chatListVm.ShowJoinGroupDialog);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task JoinGroup_CreatesPlaceholderChat(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        // Open dialog and set group ID
        chatListVm.JoinGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        var groupId = "deadbeef01020304050607080910111213141516";
        chatListVm.JoinGroupId = groupId;

        await chatListVm.ConfirmJoinGroupCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowJoinGroupDialog);
        Assert.Single(chatListVm.Chats);
        Assert.Equal(groupId, chatListVm.Chats[0].Id);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task JoinGroup_CancelClosesDialog(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await chatListVm.LoadChatsAsync();
        Dispatcher.UIThread.RunJobs();

        chatListVm.JoinGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.True(chatListVm.ShowJoinGroupDialog);

        chatListVm.CancelJoinGroupCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowJoinGroupDialog);
        Assert.Empty(chatListVm.Chats);
    }

    // --- Lookup Key Packages ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LookupKeyPackage_FindsPackage(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var targetPubKey = "aa".PadLeft(64, 'a');
        var kp = new KeyPackage
        {
            Data = new byte[256],
            NostrEventId = "kp_event_123",
            CreatedAt = DateTime.UtcNow,
            CiphersuiteId = 0x0001,
            NostrTags = new List<List<string>>
            {
                new() { "encoding", "base64" },
                new() { "mls_protocol_version", "1.0" },
                new() { "mls_ciphersuite", "0x0001" }
            }
        };
        ctx.MockNostr.Setup(n => n.FetchKeyPackagesAsync(targetPubKey))
            .ReturnsAsync((IEnumerable<KeyPackage>)new[] { kp });

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        chatListVm.NewChatParticipantInput = targetPubKey;
        await chatListVm.AddChatParticipantCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        await chatListVm.LookupKeyPackagesCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Found: 1", chatListVm.KeyPackageStatus ?? "");
        Assert.Single(chatListVm.NewChatParticipants);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LookupKeyPackage_NoPackageFound(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var targetPubKey = "bb".PadLeft(64, 'b');
        ctx.MockNostr.Setup(n => n.FetchKeyPackagesAsync(targetPubKey))
            .ReturnsAsync(Enumerable.Empty<KeyPackage>());

        var chatListVm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        Dispatcher.UIThread.RunJobs();

        chatListVm.NewChatParticipantInput = targetPubKey;
        await chatListVm.AddChatParticipantCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        await chatListVm.LookupKeyPackagesCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("No KeyPackage", chatListVm.KeyPackageStatus ?? "");
    }
}
