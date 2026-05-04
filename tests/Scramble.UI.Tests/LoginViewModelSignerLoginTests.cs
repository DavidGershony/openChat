using System.Reactive.Linq;
using Moq;
using ReactiveUI;
using Scramble.Core.Services;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Reproduces the reported Android bug where logging in via Amber/NIP-46 shows
/// "Connected" in the status text but never advances to the main view.
///
/// Drives <see cref="LoginViewModel"/> with a mocked <see cref="IExternalSigner"/>
/// whose Status observable is controllable, then asserts that:
///   1. ExternalSignerStatus = "Connected!" (the visible-to-user signal).
///   2. LoggedInUser is set (the trigger for ShellViewModel.OnLoginCompleted).
///
/// If both assertions hold, the bug is downstream of LoginViewModel — most likely
/// in ShellViewModel.OnLoginCompleted (storage init, MLS, ActivateSession) or in
/// MainActivity's IsLoggedIn observer / UI thread plumbing on Android.
///
/// If the second assertion fails while the first passes, the bug is in
/// LoginViewModel's signer-status subscription itself.
/// </summary>
public class LoginViewModelSignerLoginTests
{
    private static LoginViewModel CreateLoginViewModel(IExternalSigner signer)
    {
        var nostr = new Mock<INostrService>();
        nostr.Setup(n => n.GenerateKeyPair()).Returns(("priv", "pub", "nsec1", "npub1"));
        var qr = new Mock<IQrCodeGenerator>();
        return new LoginViewModel(nostr.Object, qr.Object, signer);
    }

    [Fact]
    public async Task SignerStatus_Connected_SetsLoggedInUser_AndStatusText()
    {
        // Arrange: a real-looking signer pubkey (32-byte hex). For nostrconnect,
        // the bunker session's remote pubkey is the same as the signing pubkey,
        // which is what makes the resulting User.IsRemoteSigner derive to true.
        var signerPubKey = new string('c', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "nc-secret")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        Assert.Null(vm.LoggedInUser);

        // Act: signer reports Connected, mirroring what HandleIncomingConnect emits
        // after an Amber approval lands on the relay listener.
        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);

        // Subscribe handler is `async _ => await HandleSignerConnectedAsync(...)`,
        // so the property update happens on a continuation. Wait briefly.
        await Task.Delay(200);

        // Assert: visible status text matches what the user sees
        Assert.Equal("Connected!", vm.ExternalSignerStatus);

        // Assert: LoggedInUser is set — this is the trigger for ShellViewModel.OnLoginCompleted.
        // If this fails, HandleSignerConnectedAsync was either skipped or threw silently.
        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(signerPubKey, vm.LoggedInUser!.PublicKeyHex);
        Assert.Null(vm.LoggedInUser.PrivateKeyHex);
        Assert.Equal(signerPubKey, vm.LoggedInUser.SignerRemotePubKey);
        Assert.True(vm.LoggedInUser.IsRemoteSigner);
    }

    [Fact]
    public async Task SignerStatus_Connected_WithNullPubKey_FallsBackToSignerProperty()
    {
        // The Amber-on-Android bug: the reconnect path in ExternalSignerService
        // emits Connected with whatever PublicKeyHex happens to be set, which
        // can be null in the status payload. LoginViewModel must fall back to
        // the signer's own PublicKeyHex property before giving up — otherwise
        // the user sees "Connected!" forever.
        var signerPubKey = new string('f', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .WithBunkerSession(remotePubKey: signerPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        // Act: Status payload has null PublicKeyHex — but the signer property is set
        signer.EmitStatus(ExternalSignerState.Connected, publicKeyHex: null);

        await Task.Delay(200);

        Assert.Equal("Connected!", vm.ExternalSignerStatus);
        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(signerPubKey, vm.LoggedInUser!.PublicKeyHex);
    }

    [Fact]
    public async Task SignerStatus_Connected_WithNullPubKey_AndNoSignerProperty_FallsBackToGetPublicKey()
    {
        // Even if the signer's PublicKeyHex property is also null, fall back to
        // GetPublicKeyAsync() before giving up.
        var signerPubKey = new string('a', 64);
        var signer = new MockExternalSignerBuilder().Build(); // no signing pubkey set
        signer.Mock.Setup(s => s.GetPublicKeyAsync()).ReturnsAsync(signerPubKey);

        var vm = CreateLoginViewModel(signer.Object);

        signer.EmitStatus(ExternalSignerState.Connected, publicKeyHex: null);
        await Task.Delay(200);

        Assert.Equal("Connected!", vm.ExternalSignerStatus);
        Assert.NotNull(vm.LoggedInUser);
        Assert.Equal(signerPubKey, vm.LoggedInUser!.PublicKeyHex);
        signer.Mock.Verify(s => s.GetPublicKeyAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SignerStatus_Connected_FiredTwice_OnlyLogsInOnce()
    {
        // Arrange: simulates a transient flap where Connected fires more than once
        // (e.g. an explicit Connected + a reconnect-path Connected).
        var signerPubKey = new string('d', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        var loggedInUserChanges = 0;
        vm.WhenAnyValue(x => x.LoggedInUser)
            .Where(u => u != null)
            .Subscribe(_ => loggedInUserChanges++);

        // Act: fire Connected twice
        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);
        await Task.Delay(50);
        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);
        await Task.Delay(200);

        // Assert: LoggedInUser was only assigned a non-null value once
        // (HandleSignerConnectedAsync has an early-return guard for this).
        Assert.Equal(1, loggedInUserChanges);
        Assert.NotNull(vm.LoggedInUser);
    }

    [Fact]
    public async Task SignerStatus_Connected_TransportPubKeyDiffersFromSigningPubKey_UsesSigningKey()
    {
        // Reproducer for the wrong-npub bug: Amber sends NIP-46 messages from
        // a transport pubkey that's different from the user's actual signing
        // pubkey. HandleIncomingConnect captures the transport pubkey (the
        // event.pubkey of kind 24133) and emits it via Status — but that's NOT
        // the user's identity. The fix calls get_public_key to resolve the
        // actual signing key, and User.PublicKeyHex / User.Npub must reflect
        // that, not the transport key.
        var transportPubKey = new string('1', 64);
        var actualSigningPubKey = new string('2', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(transportPubKey)             // service property holds transport key
            .WithGetPublicKeyResponse(actualSigningPubKey)  // get_public_key returns the real one
            .WithBunkerSession(remotePubKey: transportPubKey, secret: "s")
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        // Status fires with the transport pubkey (what HandleIncomingConnect emits)
        signer.EmitStatus(ExternalSignerState.Connected, transportPubKey);
        await Task.Delay(200);

        Assert.NotNull(vm.LoggedInUser);
        // User identity must be the actual signing pubkey, not the transport key
        Assert.Equal(actualSigningPubKey, vm.LoggedInUser!.PublicKeyHex);
        // Npub must be derived from the same pubkey (the wrong-npub bug was
        // the User.Npub coming from one source and PublicKeyHex from another).
        var expectedNpub = Scramble.Core.Crypto.Bech32.Encode(
            "npub", Convert.FromHexString(actualSigningPubKey));
        Assert.Equal(expectedNpub, vm.LoggedInUser.Npub);
        signer.Mock.Verify(s => s.ResolveSigningPubKeyAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SignerStatus_StateProgression_UpdatesUiSignals()
    {
        // Arrange
        var signerPubKey = new string('e', 64);
        var signer = new MockExternalSignerBuilder()
            .WithSigningPubKey(signerPubKey)
            .Build();
        var vm = CreateLoginViewModel(signer.Object);

        // Act + Assert: walk through the full handshake the UI binds to.
        signer.EmitStatus(ExternalSignerState.Connecting);
        await Task.Delay(50);
        Assert.Equal("Connecting to relay...", vm.ExternalSignerStatus);
        Assert.True(vm.IsExternalSignerConnecting);

        signer.EmitStatus(ExternalSignerState.WaitingForApproval);
        await Task.Delay(50);
        Assert.Equal("Scan the QR code with your signer app...", vm.ExternalSignerStatus);
        Assert.True(vm.IsExternalSignerConnecting);

        signer.EmitStatus(ExternalSignerState.Connected, signerPubKey);
        await Task.Delay(200);
        Assert.Equal("Connected!", vm.ExternalSignerStatus);
        Assert.False(vm.IsExternalSignerConnecting);
        Assert.NotNull(vm.LoggedInUser);
    }
}
