using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.UI.Tests;

/// <summary>
/// Headless tests for group member management: add, remove, and leave.
/// </summary>
public class HeadlessGroupMemberTests : HeadlessTestBase
{
    private async Task<(RealTestContext Creator, RealTestContext Joiner, Chat Chat)> CreateGroupWithTwoUsers(string backend)
    {
        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        var joiner = await CreateRealContext(backend);
        await joiner.MessageService.InitializeAsync();

        // Creator creates a group
        var groupInfo = await creator.MlsService.CreateGroupAsync("Member Test Group", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Member Test Group",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // Generate joiner's KeyPackage
        var joinerKp = await joiner.MlsService.GenerateKeyPackageAsync();
        PrepareKeyPackageForAddMember(joinerKp, joiner.User.PublicKeyHex);

        // Mock fetching joiner's KeyPackage from relays
        creator.MockNostr.Setup(n => n.FetchKeyPackagesAsync(joiner.User.PublicKeyHex))
            .ReturnsAsync((IEnumerable<KeyPackage>)new[] { joinerKp });

        return (creator, joiner, chat);
    }

    // --- Add Member ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AddMember_UpdatesParticipantList(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        Assert.Single(chat.ParticipantPublicKeys);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Contains(joiner.User.PublicKeyHex, stored!.ParticipantPublicKeys);
        Assert.Equal(2, stored.ParticipantPublicKeys.Count);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AddMember_PublishesWelcomeToRelay(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        // Verify Welcome was published
        creator.MockNostr.Verify(n => n.PublishWelcomeAsync(
            It.IsAny<byte[]>(), joiner.User.PublicKeyHex, It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // --- Remove Member ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RemoveMember_UpdatesParticipantList(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        // First add the member
        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);
        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Equal(2, stored!.ParticipantPublicKeys.Count);

        // Then remove
        await creator.MessageService.RemoveMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.DoesNotContain(joiner.User.PublicKeyHex, stored!.ParticipantPublicKeys);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RemoveMember_PublishesCommit(string backend)
    {
        if (ShouldSkip(backend)) return;
        var (creator, joiner, chat) = await CreateGroupWithTwoUsers(backend);

        await creator.MessageService.AddMemberAsync(chat.Id, joiner.User.PublicKeyHex);
        await creator.MessageService.RemoveMemberAsync(chat.Id, joiner.User.PublicKeyHex);

        // Verify commit was published (PublishGroupMessageAsync for the removal commit)
        creator.MockNostr.Verify(n => n.PublishGroupMessageAsync(
            It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    // --- Leave Group ---

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task LeaveGroup_DeletesLocalState(string backend)
    {
        if (ShouldSkip(backend)) return;
        var creator = await CreateRealContext(backend);
        await creator.MessageService.InitializeAsync();

        var groupInfo = await creator.MlsService.CreateGroupAsync("Leave Test", new[] { "wss://relay.test" });
        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Leave Test",
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { creator.User.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        await creator.Storage.SaveChatAsync(chat);

        // LeaveGroup cleans up local state (MLS + chat) without self-removal commit (RFC 9420)
        await creator.MessageService.LeaveGroupAsync(chat.Id);

        var stored = await creator.Storage.GetChatAsync(chat.Id);
        Assert.Null(stored);
    }
}
