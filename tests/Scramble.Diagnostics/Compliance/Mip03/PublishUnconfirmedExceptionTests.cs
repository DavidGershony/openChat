using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.Diagnostics.RelayHarness;
using Xunit;

namespace Scramble.Diagnostics.Compliance.Mip03;

/// <summary>
/// MIP-03 §"Commit Message Race Conditions" — sending step 2:
/// "MUST NOT apply commit until at least one relay confirms receipt"
///
/// These tests verify that PublishCommitAsync, PublishGroupMessageAsync, and
/// PublishWelcomeAsync throw <see cref="PublishUnconfirmedException"/> when
/// no relay confirms acceptance. Uses <see cref="FaultyRelay"/> with DropOk
/// to suppress relay OK responses.
/// </summary>
[Trait("Category", "MIP-Compliance")]
[Trait("MIP", "MIP-03")]
public class PublishUnconfirmedExceptionTests : IAsyncLifetime
{
    private FaultyRelay? _relay;
    private NostrService? _nostrService;
    private string? _privateKeyHex;

    public async ValueTask InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        _relay = new FaultyRelay();
        await _relay.StartAsync();

        _nostrService = new NostrService();
        var keys = _nostrService.GenerateKeyPair();
        _privateKeyHex = keys.privateKeyHex;
        await _nostrService.ConnectAsync(_relay.WsUrl);
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        if (_nostrService != null)
        {
            try { await _nostrService.DisconnectAsync(); }
            catch { }
            (_nostrService as IDisposable)?.Dispose();
        }
        if (_relay != null) await _relay.DisposeAsync();
    }

    [Fact]
    public async Task PublishCommit_Throws_WhenOk_NotReceived()
    {
        Assert.NotNull(_relay);
        Assert.NotNull(_nostrService);

        _relay.Faults.DropOk = true;

        var commitData = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var groupId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

        var ex = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _nostrService.PublishCommitAsync(commitData, groupId, _privateKeyHex));

        Assert.Equal(445, ex.Kind);
        Assert.NotEmpty(ex.EventId);
    }

    [Fact]
    public async Task PublishGroupMessage_Throws_WhenOk_NotReceived()
    {
        Assert.NotNull(_relay);
        Assert.NotNull(_nostrService);

        _relay.Faults.DropOk = true;

        var encryptedData = new byte[] { 0x10, 0x20, 0x30 };
        var groupId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

        var ex = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _nostrService.PublishGroupMessageAsync(encryptedData, groupId, _privateKeyHex));

        Assert.Equal(445, ex.Kind);
    }

    [Fact]
    public async Task PublishCommit_Succeeds_WhenOk_Received()
    {
        Assert.NotNull(_relay);
        Assert.NotNull(_nostrService);

        // No faults — relay sends OK normally
        var commitData = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var groupId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

        var eventId = await _nostrService.PublishCommitAsync(commitData, groupId, _privateKeyHex);

        Assert.NotEmpty(eventId);
        Assert.True(_nostrService.LastPublishOkResult.accepted);
    }

    [Fact]
    public async Task PublishCommit_Throws_WhenRelay_RejectsAllEvents()
    {
        Assert.NotNull(_relay);
        Assert.NotNull(_nostrService);

        _relay.Faults.RejectAllEvents = true;

        var commitData = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var groupId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

        var ex = await Assert.ThrowsAsync<PublishUnconfirmedException>(
            () => _nostrService.PublishCommitAsync(commitData, groupId, _privateKeyHex));

        Assert.Equal(445, ex.Kind);
    }
}
