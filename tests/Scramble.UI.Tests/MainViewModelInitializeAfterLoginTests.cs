using Moq;
using Scramble.Core.Crypto;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Tests.TestHelpers;
using Xunit;

namespace Scramble.UI.Tests;

/// <summary>
/// Reproduces the production data-corruption bug observed in user logs:
///   `Session activated for npub1axcr6lf... in profile 9ce249b56250ea4f`
/// The current_user row in profile 9ce249b…'s database had been overwritten with
/// a foreign user's identity (robo-smurf), so switching to "The Dude" loaded
/// the wrong CurrentUser into MainViewModel — the chat list was The Dude's,
/// the top-left header was Robo-smurf.
///
/// Mechanism (traced from MainViewModel.InitializeAfterLoginAsync ~line 344-356):
/// when the signer reports a PublicKeyHex that differs from the stored
/// CurrentUser.PublicKeyHex, the code mutates the in-memory User and calls
/// SaveCurrentUserAsync — overwriting the row in whatever profile DB is
/// currently active. If the signer state is stale or wrong (e.g. it carries
/// a previous account's pubkey, or a relay returns a different signer's
/// reply), this writes a foreign identity into the active profile's DB.
///
/// The test asserts this DOES NOT happen: a saved user's PublicKeyHex must
/// not change just because the signer reports something different.
/// </summary>
public class MainViewModelInitializeAfterLoginTests : HeadlessTestBase
{
    [Fact]
    public async Task InitializeAfterLogin_StaleSignerReportsForeignPubKey_DoesNotCorruptUserRow()
    {
        // Arrange: build a signer-based account stored in profile A's DB.
        // (CreateRealContext gives us a local-key user; convert into a signer-based
        // one by clearing the private key and filling the persisted-signer-session
        // fields, then save.)
        var ctx = await CreateRealContext("managed", saveUser: false);
        var profileAPubKey = ctx.User.PublicKeyHex;
        var profileANpub = Bech32.Encode("npub", Convert.FromHexString(profileAPubKey));

        var signerUser = new User
        {
            Id = ctx.User.Id,
            PublicKeyHex = profileAPubKey,
            Npub = profileANpub,
            DisplayName = "Profile A user",
            CreatedAt = DateTime.UtcNow,
            IsCurrentUser = true,
            // Signer fields populated → InitializeAfterLoginAsync will attempt RestoreSessionAsync
            PrivateKeyHex = null,
            Nsec = null,
            SignerRelayUrl = "wss://signer.test",
            SignerRemotePubKey = profileAPubKey,
            SignerSecret = "secret-a",
            SignerLocalPrivateKeyHex = new string('1', 64),
            SignerLocalPublicKeyHex = new string('2', 64)
        };
        await ctx.Storage.SaveCurrentUserAsync(signerUser);

        // A foreign signing pubkey — what a stale or misrouted signer reply might return.
        // (In the real bug it was a different account's signing key entirely.)
        var foreignPubKey = new string('f', 64);
        Assert.NotEqual(profileAPubKey, foreignPubKey);

        var mainVm = CreateMainViewModel(ctx);
        mainVm.CurrentUser = signerUser;

        // Mock a signer whose RestoreSession succeeds and reports the foreign pubkey
        // — i.e. PublicKeyHex != CurrentUser.PublicKeyHex when restore returns.
        var signer = new MockExternalSignerBuilder()
            .Disconnected()                                  // forces InitializeAfterLogin into the restore branch
            .WithRestoreSession(succeeds: true, resolvedPubKeyHex: foreignPubKey)
            .Build();
        mainVm.ExternalSigner = signer.Object;

        // Act
        await mainVm.InitializeAfterLoginAsync();

        // Assert: the User row in profile A's DB MUST still be Profile A's user.
        // Failing this assertion = the bug exists (foreign pubkey was written into
        // profile A's current_user, exactly as observed in the production log).
        var afterRestore = await ctx.Storage.GetCurrentUserAsync();
        Assert.NotNull(afterRestore);
        Assert.Equal(profileAPubKey, afterRestore!.PublicKeyHex);
        Assert.Equal(profileANpub, afterRestore.Npub);
    }
}
