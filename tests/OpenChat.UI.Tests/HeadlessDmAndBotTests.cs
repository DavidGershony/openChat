using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Presentation.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

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

        Assert.Single(chatListVm.Chats);
        Assert.True(chatListVm.Chats[0].IsBot);
        Assert.False(chatListVm.Chats[0].IsGroup);
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
        await chatListVm.CreateBotChatCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.False(chatListVm.ShowAddBotDialog);
        Assert.Single(chatListVm.Chats);
        Assert.True(chatListVm.Chats[0].IsBot);
    }
}
