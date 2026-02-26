using OpenChat.Core.Marmot;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Integration tests for MarmotWrapper using real secp256k1 keys.
/// When the native DLL is present these exercise the real native MLS library.
/// </summary>
public class MarmotWrapperTests : IAsyncLifetime
{
    private MarmotWrapper _wrapper = null!;
    private string _privateKey = null!;
    private string _publicKey = null!;

    public async Task InitializeAsync()
    {
        var nostrService = new NostrService();
        (_privateKey, _publicKey, _, _) = nostrService.GenerateKeyPair();

        _wrapper = new MarmotWrapper();
        await _wrapper.InitializeAsync(_privateKey, _publicKey);
    }

    public Task DisposeAsync()
    {
        _wrapper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GenerateKeyPackage_ShouldReturnDataAndTags()
    {
        var result = await _wrapper.GenerateKeyPackageAsync();

        Assert.NotNull(result);
        Assert.NotEmpty(result.Data);
        Assert.NotEmpty(result.Content);
        Assert.NotEmpty(result.Tags);

        // Content should be valid base64 that matches Data
        var decoded = Convert.FromBase64String(result.Content);
        Assert.Equal(result.Data, decoded);
    }

    [Fact]
    public async Task CreateGroup_ShouldReturnGroupIdAndEpoch()
    {
        var (groupId, epoch) = await _wrapper.CreateGroupAsync("Test Group");

        Assert.NotNull(groupId);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task EncryptDecryptMessage_ShouldRoundTrip()
    {
        var (groupId, _) = await _wrapper.CreateGroupAsync("Encryption Test Group");
        var plaintext = "Hello, MLS!";

        var ciphertext = await _wrapper.EncryptMessageAsync(groupId, plaintext);
        var (senderPublicKey, decrypted, epoch) = await _wrapper.DecryptMessageAsync(groupId, ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task GetGroupInfo_ShouldReturnCorrectInfo()
    {
        var groupName = "Info Test Group";
        var (groupId, _) = await _wrapper.CreateGroupAsync(groupName);

        var info = await _wrapper.GetGroupInfoAsync(groupId);

        Assert.NotNull(info);
        Assert.Equal(groupName, info.Value.groupName);
    }

    [Fact]
    public async Task ExportGroupState_ShouldReturnData()
    {
        var groupName = "State Test Group";
        var (groupId, _) = await _wrapper.CreateGroupAsync(groupName);

        var state = await _wrapper.ExportGroupStateAsync(groupId);

        Assert.NotNull(state);
        Assert.NotEmpty(state);
    }

    [Fact]
    public async Task UpdateKeys_ShouldReturnCommitData()
    {
        var (groupId, _) = await _wrapper.CreateGroupAsync("Update Keys Test");

        var commitData = await _wrapper.UpdateKeysAsync(groupId);

        Assert.NotNull(commitData);
        Assert.NotEmpty(commitData);
    }
}
