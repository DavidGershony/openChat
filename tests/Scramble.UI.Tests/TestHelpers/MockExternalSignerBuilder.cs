using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Scramble.Core.Crypto;
using Scramble.Core.Services;

namespace Scramble.UI.Tests.TestHelpers;

/// <summary>
/// Fluent builder for a mocked <see cref="IExternalSigner"/> with a controllable
/// <see cref="IExternalSigner.Status"/> stream. Lets tests simulate the full NIP-46
/// connection lifecycle (Connecting → WaitingForApproval → Connected, or Error
/// transitions) without spinning up a real WebSocket / Amber instance.
///
/// Example:
///   var signer = new MockExternalSignerBuilder()
///       .WithSigningPubKey(pubKeyHex)
///       .WithBunkerSession(remotePubKey: pubKeyHex, secret: "s")
///       .Build();
///
///   var vm = new LoginViewModel(nostr, qr, signer.Object);
///   signer.EmitStatus(ExternalSignerState.Connected, pubKeyHex);
///   await Task.Delay(200);
///   Assert.NotNull(vm.LoggedInUser);
/// </summary>
public sealed class MockExternalSignerBuilder
{
    private string? _publicKeyHex;
    private string? _remotePubKey;
    private string? _secret;
    private string _localPriv = new('a', 64);
    private string _localPub = new('b', 64);
    private List<string> _relays = new() { "wss://relay.test" };
    private bool _isConnected = true;
    private string? _signEventResponse;
    private string? _getPublicKeyResponse;

    /// <summary>The signer's claimed identity (the user's pubkey, in hex).</summary>
    public MockExternalSignerBuilder WithSigningPubKey(string pubKeyHex)
    {
        _publicKeyHex = pubKeyHex;
        return this;
    }

    /// <summary>NIP-46 session metadata (what RestoreSessionAsync would persist).</summary>
    public MockExternalSignerBuilder WithBunkerSession(string remotePubKey, string secret)
    {
        _remotePubKey = remotePubKey;
        _secret = secret;
        return this;
    }

    /// <summary>The ephemeral keypair used as the NIP-46 client identity. Defaults to fixed dummy values.</summary>
    public MockExternalSignerBuilder WithLocalEphemeralKeys(string privHex, string pubHex)
    {
        _localPriv = privHex;
        _localPub = pubHex;
        return this;
    }

    public MockExternalSignerBuilder WithRelays(params string[] relays)
    {
        _relays = relays.ToList();
        return this;
    }

    /// <summary>Marks the signer as not connected (default is connected).</summary>
    public MockExternalSignerBuilder Disconnected()
    {
        _isConnected = false;
        return this;
    }

    /// <summary>Stub SignEventAsync to return a fixed signed-event JSON string.</summary>
    public MockExternalSignerBuilder WithSignEventResponse(string signedJson)
    {
        _signEventResponse = signedJson;
        return this;
    }

    /// <summary>
    /// Stub GetPublicKeyAsync (NIP-46 get_public_key) to return a specific hex
    /// pubkey. LoginViewModel calls this on connect to resolve the actual signing
    /// key (Amber's transport pubkey can differ from its signing pubkey).
    /// Defaults to whatever WithSigningPubKey was set to.
    /// </summary>
    public MockExternalSignerBuilder WithGetPublicKeyResponse(string pubKeyHex)
    {
        _getPublicKeyResponse = pubKeyHex;
        return this;
    }

    public MockExternalSigner Build()
    {
        var signer = new MockExternalSigner(
            _publicKeyHex, _remotePubKey, _secret, _localPriv, _localPub, _relays, _isConnected);
        if (_signEventResponse != null)
            signer.Mock.Setup(s => s.SignEventAsync(It.IsAny<UnsignedNostrEvent>()))
                .ReturnsAsync(_signEventResponse);

        // Default GetPublicKeyAsync to the configured signing pubkey so tests
        // that don't care about the transport-vs-signing-key distinction don't
        // need to wire it up explicitly. ResolveSigningPubKeyAsync mirrors it
        // (same return value) — production code routes through Resolve, but
        // tests can verify either was called.
        var pubKeyToReturn = _getPublicKeyResponse ?? _publicKeyHex;
        if (pubKeyToReturn != null)
        {
            signer.Mock.Setup(s => s.GetPublicKeyAsync()).ReturnsAsync(pubKeyToReturn);
            signer.Mock.Setup(s => s.ResolveSigningPubKeyAsync()).ReturnsAsync(pubKeyToReturn);
        }
        return signer;
    }
}

/// <summary>
/// Wraps a <see cref="Mock{T}"/> of <see cref="IExternalSigner"/> together with
/// the <see cref="Subject{T}"/> that backs its Status observable, so callers can
/// drive state transitions inline.
/// </summary>
public sealed class MockExternalSigner
{
    public Mock<IExternalSigner> Mock { get; }
    public IExternalSigner Object => Mock.Object;

    /// <summary>Subject that backs the signer's Status observable. Call OnNext or use EmitStatus.</summary>
    public Subject<ExternalSignerStatus> StatusController { get; } = new();

    public string? PublicKeyHex { get; }
    public string? RemotePubKey { get; }
    public string? Secret { get; }

    internal MockExternalSigner(
        string? publicKeyHex,
        string? remotePubKey,
        string? secret,
        string localPriv,
        string localPub,
        List<string> relays,
        bool isConnected)
    {
        PublicKeyHex = publicKeyHex;
        RemotePubKey = remotePubKey;
        Secret = secret;

        Mock = new Mock<IExternalSigner>();
        Mock.SetupGet(s => s.Status).Returns(StatusController.AsObservable());
        Mock.SetupGet(s => s.PublicKeyHex).Returns(publicKeyHex);
        Mock.SetupGet(s => s.Npub).Returns(
            publicKeyHex != null ? Bech32.Encode("npub", Convert.FromHexString(publicKeyHex)) : null);
        Mock.SetupGet(s => s.RelayUrls).Returns(relays);
        Mock.SetupGet(s => s.RelayUrl).Returns(relays.FirstOrDefault());
        Mock.SetupGet(s => s.RemotePubKey).Returns(remotePubKey);
        Mock.SetupGet(s => s.Secret).Returns(secret);
        Mock.SetupGet(s => s.LocalPrivateKeyHex).Returns(localPriv);
        Mock.SetupGet(s => s.LocalPublicKeyHex).Returns(localPub);
        Mock.SetupGet(s => s.IsConnected).Returns(isConnected);
        Mock.Setup(s => s.DisconnectAsync()).Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Pushes a status event through the Status observable. Mirrors what the real
    /// ExternalSignerService emits at each stage of the NIP-46 handshake.
    /// </summary>
    public void EmitStatus(ExternalSignerState state, string? publicKeyHex = null, string? error = null)
    {
        StatusController.OnNext(new ExternalSignerStatus
        {
            State = state,
            IsConnected = state == ExternalSignerState.Connected,
            PublicKeyHex = publicKeyHex,
            Error = error
        });
    }
}
