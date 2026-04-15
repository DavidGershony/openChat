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
        vm.NewGroupCommand.Execute().Subscribe();
        return vm;
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_AcceptsHexPubkey()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewGroupParticipantInput = hex;
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewGroupParticipants);
        Assert.Equal(hex, vm.NewGroupParticipants[0].PublicKeyHex);
        Assert.Equal(string.Empty, vm.NewGroupParticipantInput);
        Assert.Null(vm.NewGroupError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_AcceptsNpubAndConvertsToHex()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();
        var npub = Bech32.Encode("npub", Convert.FromHexString(hex));

        vm.NewGroupParticipantInput = npub;
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewGroupParticipants);
        Assert.Equal(hex, vm.NewGroupParticipants[0].PublicKeyHex);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_RejectsGarbage()
    {
        var vm = await CreateVmAsync();

        vm.NewGroupParticipantInput = "not-a-key";
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Empty(vm.NewGroupParticipants);
        Assert.NotNull(vm.NewGroupError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_RejectsInvalidNpub()
    {
        var vm = await CreateVmAsync();

        vm.NewGroupParticipantInput = "npub1garbage";
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Empty(vm.NewGroupParticipants);
        Assert.NotNull(vm.NewGroupError);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_DedupesExactMatch()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewGroupParticipantInput = hex;
        vm.AddGroupParticipantCommand.Execute().Subscribe();
        vm.NewGroupParticipantInput = hex;
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewGroupParticipants);
    }

    [AvaloniaFact]
    public async Task AddGroupParticipant_IsCaseInsensitive()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewGroupParticipantInput = hex;
        vm.AddGroupParticipantCommand.Execute().Subscribe();
        vm.NewGroupParticipantInput = hex.ToUpperInvariant();
        vm.AddGroupParticipantCommand.Execute().Subscribe();

        Assert.Single(vm.NewGroupParticipants);
    }

    [AvaloniaFact]
    public async Task RemoveGroupParticipant_RemovesTheChip()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.NewGroupParticipantInput = hex;
        vm.AddGroupParticipantCommand.Execute().Subscribe();
        var chip = vm.NewGroupParticipants[0];

        vm.RemoveGroupParticipantCommand.Execute(chip).Subscribe();

        Assert.Empty(vm.NewGroupParticipants);
    }

    [AvaloniaFact]
    public async Task AddContactToGroupCommand_AddsByHex()
    {
        var vm = await CreateVmAsync();
        var hex = RandomHex64();

        vm.AddContactToGroupCommand.Execute(hex).Subscribe();

        Assert.Single(vm.NewGroupParticipants);
        Assert.Equal(hex, vm.NewGroupParticipants[0].PublicKeyHex);
    }

    [AvaloniaFact]
    public async Task NewGroupCommand_ResetsParticipants()
    {
        var vm = await CreateVmAsync();
        vm.AddContactToGroupCommand.Execute(RandomHex64()).Subscribe();
        Assert.Single(vm.NewGroupParticipants);

        vm.NewGroupCommand.Execute().Subscribe();

        Assert.Empty(vm.NewGroupParticipants);
        Assert.Equal(string.Empty, vm.NewGroupParticipantInput);
    }
}
