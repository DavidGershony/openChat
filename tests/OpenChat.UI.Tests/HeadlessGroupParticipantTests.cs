using Avalonia.Headless.XUnit;
using OpenChat.Core.Crypto;
using OpenChat.Presentation.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

public class HeadlessGroupParticipantTests : HeadlessTestBase
{
    private static string RandomHex64()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<ChatListViewModel> CreateVmAsync()
    {
        var ctx = await CreateRealContext("managed");
        var vm = new ChatListViewModel(ctx.MessageService, ctx.Storage, ctx.MlsService, ctx.MockNostr.Object);
        await vm.LoadChatsAsync();
        vm.NewChatCommand.Execute().Subscribe();
        return vm;
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_AcceptsHexPubkey()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewChatParticipantInput = hex;
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewChatParticipants);
        Assert.Equal(hex, vm.NewChatParticipants[0].PublicKeyHex);
        Assert.Equal(string.Empty, vm.NewChatParticipantInput);
        Assert.Null(vm.NewChatError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_AcceptsNpubAndConvertsToHex()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();
        var npub = Bech32.Encode("npub", Convert.FromHexString(hex));

        vm.NewChatParticipantInput = npub;
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewChatParticipants);
        Assert.Equal(hex, vm.NewChatParticipants[0].PublicKeyHex);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_RejectsGarbage()
    {
        var vm = await CreateVmAsync();

        vm.NewChatParticipantInput = "not-a-key";
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Empty(vm.NewChatParticipants);
        Assert.NotNull(vm.NewChatError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_RejectsInvalidNpub()
    {
        var vm = await CreateVmAsync();

        vm.NewChatParticipantInput = "npub1garbage";
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Empty(vm.NewChatParticipants);
        Assert.NotNull(vm.NewChatError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_DedupesExactMatch()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewChatParticipantInput = hex;
        vm.AddChatParticipantCommand.Execute().Subscribe();
        vm.NewChatParticipantInput = hex;
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewChatParticipants);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_IsCaseInsensitive()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewChatParticipantInput = hex;
        vm.AddChatParticipantCommand.Execute().Subscribe();
        vm.NewChatParticipantInput = hex.ToUpperInvariant();
        vm.AddChatParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewChatParticipants);
    }

    [AvaloniaFact]
    public async Task RemoveGroupParticipant_RemovesTheChip()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewChatParticipantInput = hex;
        vm.AddChatParticipantCommand.Execute().Subscribe();
        var chip = vm.NewChatParticipants[0];

        vm.RemoveChatParticipantCommand.Execute(chip).Subscribe();

        Assert.Empty(vm.NewChatParticipants);
    }

    [AvaloniaFact]
    public async Task AddContactToChatCommand_AddsByHex()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.AddContactToChatCommand.Execute(hex).Subscribe();

        Assert.Single(vm.NewChatParticipants);
        Assert.Equal(hex, vm.NewChatParticipants[0].PublicKeyHex);
    }

    [AvaloniaFact]
    public async Task NewChatCommand_ResetsParticipants()
    {
        var vm = await CreateVmAsync();
        vm.AddContactToChatCommand.Execute(RandomHex64()).Subscribe();
        Assert.Single(vm.NewChatParticipants);

        vm.NewChatCommand.Execute().Subscribe();

        Assert.Empty(vm.NewChatParticipants);
        Assert.Equal(string.Empty, vm.NewChatParticipantInput);
    }
}
