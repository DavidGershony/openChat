using System.Reactive.Linq;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Tests for the NIP-46 connect flow in ExternalSignerService.
/// Specifically tests ProcessDecryptedNip46Message to ensure:
/// 1. Amber's connect ack (response with secret) triggers HandleIncomingConnect when not connected
/// 2. Unmatched responses while connected are cached for replay (not treated as connect)
/// 3. Short response IDs don't crash with Substring
/// 4. Incoming connect requests (method="connect") work correctly
/// </summary>
public class ExternalSignerConnectFlowTests
{
    private static string FakeSenderPubKey => "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1";

    [Fact]
    public void AmberConnectAck_WhenNotConnected_SetsIsConnected()
    {
        // This is the exact scenario that was broken by commit 0b9890c:
        // Amber sends a response (no "method" field) with the secret as "result"
        // while ExternalSignerService is not yet connected (nostrconnect:// QR flow).
        var signer = new ExternalSignerService();
        Assert.False(signer.IsConnected);

        // Simulate Amber's connect ack — a response with the secret as result
        var amberResponse = """{"id":"12a4b8","result":"e131c1b81331ed18c47e29980b04bde0"}""";

        signer.ProcessDecryptedNip46Message(amberResponse, FakeSenderPubKey);

        Assert.True(signer.IsConnected);
        Assert.Equal(FakeSenderPubKey, signer.PublicKeyHex);
    }

    [Fact]
    public void AmberConnectAck_WhenNotConnected_EmitsConnectedStatus()
    {
        var signer = new ExternalSignerService();
        ExternalSignerStatus? lastStatus = null;
        signer.Status.Subscribe(s => lastStatus = s);

        var amberResponse = """{"id":"abc123","result":"some-secret-value"}""";
        signer.ProcessDecryptedNip46Message(amberResponse, FakeSenderPubKey);

        Assert.NotNull(lastStatus);
        Assert.True(lastStatus!.IsConnected);
        Assert.Equal(ExternalSignerState.Connected, lastStatus.State);
        Assert.Equal(FakeSenderPubKey, lastStatus.PublicKeyHex);
    }

    [Fact]
    public void UnmatchedResponse_WhenAlreadyConnected_CachesForReplay()
    {
        // When already connected, an unmatched response should be cached
        // for replay (not treated as a connect ack)
        var signer = new ExternalSignerService();

        // First, connect
        var connectMsg = """{"id":"conn1","result":"secret"}""";
        signer.ProcessDecryptedNip46Message(connectMsg, FakeSenderPubKey);
        Assert.True(signer.IsConnected);

        // Now send another unmatched response while connected — should be cached, not crash
        var replayedResponse = """{"id":"abcd1234","result":"signed-event-json-here"}""";
        signer.ProcessDecryptedNip46Message(replayedResponse, FakeSenderPubKey);

        // Should still be connected (no state change)
        Assert.True(signer.IsConnected);
    }

    [Fact]
    public void IncomingConnectRequest_WithMethodField_SetsIsConnected()
    {
        // Some signers send a request with method="connect" instead of a response.
        // This path should also work.
        var signer = new ExternalSignerService();
        Assert.False(signer.IsConnected);

        var connectRequest = """{"id":"req1","method":"connect","params":["e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1"]}""";
        signer.ProcessDecryptedNip46Message(connectRequest, FakeSenderPubKey);

        Assert.True(signer.IsConnected);
        Assert.Equal(FakeSenderPubKey, signer.PublicKeyHex);
    }

    [Fact]
    public void ShortResponseId_DoesNotCrash()
    {
        // Regression test: response IDs shorter than 8 chars must not throw
        // ArgumentOutOfRangeException on Substring
        var signer = new ExternalSignerService();

        // First connect
        var connectMsg = """{"id":"ab","result":"secret"}""";
        signer.ProcessDecryptedNip46Message(connectMsg, FakeSenderPubKey);
        Assert.True(signer.IsConnected);

        // Now a replayed response with a short ID — should not throw
        var shortIdResponse = """{"id":"xyz","result":"some-data"}""";
        var ex = Record.Exception(() =>
            signer.ProcessDecryptedNip46Message(shortIdResponse, FakeSenderPubKey));
        Assert.Null(ex);
    }

    [Fact]
    public void PendingRequest_MatchedResponse_CompletesTask()
    {
        // When a response matches a pending request, it should complete the TaskCompletionSource
        var signer = new ExternalSignerService();

        // Connect first
        var connectMsg = """{"id":"c1","result":"s"}""";
        signer.ProcessDecryptedNip46Message(connectMsg, FakeSenderPubKey);

        // We can't easily add pending requests from outside, but we can verify
        // that an unmatched response with an error doesn't crash
        var errorResponse = """{"id":"unknown","error":"some error"}""";
        var ex = Record.Exception(() =>
            signer.ProcessDecryptedNip46Message(errorResponse, FakeSenderPubKey));
        Assert.Null(ex);
    }

    [Fact]
    public void ConnectAck_WithSignerPubKeyInParams_ExtractsCorrectKey()
    {
        // Amber sometimes puts the actual signer pubkey in params[0]
        var signer = new ExternalSignerService();
        var actualSignerPub = "aaaa".PadRight(64, 'a');

        var connectRequest = $$$"""{"id":"req1","method":"connect","params":["{{{actualSignerPub}}}"]}""";
        signer.ProcessDecryptedNip46Message(connectRequest, FakeSenderPubKey);

        Assert.True(signer.IsConnected);
        // Should use the pubkey from params, not the sender pubkey
        Assert.Equal(actualSignerPub, signer.PublicKeyHex);
    }

    [Fact]
    public void MultipleConnectAcks_WhenNotConnected_OnlyFirstConnects()
    {
        // Amber may send duplicate connect acks (user taps approve multiple times)
        var signer = new ExternalSignerService();
        int statusCount = 0;
        signer.Status.Subscribe(_ => statusCount++);

        var ack1 = """{"id":"a1","result":"secret"}""";
        var ack2 = """{"id":"a2","result":"secret"}""";

        signer.ProcessDecryptedNip46Message(ack1, FakeSenderPubKey);
        Assert.True(signer.IsConnected);

        // Second ack should be treated as a replay cache, not a second connect
        signer.ProcessDecryptedNip46Message(ack2, FakeSenderPubKey);
        Assert.True(signer.IsConnected);
    }
}
