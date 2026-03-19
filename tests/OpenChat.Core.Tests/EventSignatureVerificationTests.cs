using System.Text;
using NBitcoin.Secp256k1;
using OpenChat.Core.Services;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace OpenChat.Core.Tests;

/// <summary>
/// Proves that forged Nostr events are rejected by signature verification.
/// Tests the VerifyEventSignature method that guards all event parsing from relays.
/// </summary>
public class EventSignatureVerificationTests
{
    private readonly NostrService _nostrService = new();

    /// <summary>
    /// Creates a valid, signed Nostr event and returns all its components.
    /// </summary>
    private (string id, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
        CreateSignedEvent(string? privateKeyHex = null, int kind = 1,
            string content = "hello", List<List<string>>? tags = null)
    {
        if (privateKeyHex == null)
        {
            var (priv, _, _, _) = _nostrService.GenerateKeyPair();
            privateKeyHex = priv;
        }

        var privBytes = Convert.FromHexString(privateKeyHex);
        var pubBytes = NostrService.DerivePublicKey(privBytes);
        var pubkey = Convert.ToHexString(pubBytes).ToLowerInvariant();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        tags ??= new List<List<string>>();

        var serialized = NostrService.SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var id = Convert.ToHexString(idBytes).ToLowerInvariant();
        var sigBytes = NostrService.SignSchnorr(idBytes, privBytes);
        var sig = Convert.ToHexString(sigBytes).ToLowerInvariant();

        return (id, pubkey, createdAt, kind, tags, content, sig);
    }

    #region Valid events pass

    [Fact]
    public void ValidEvent_PassesVerification()
    {
        var (id, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_WithTags_PassesVerification()
    {
        var tags = new List<List<string>>
        {
            new() { "p", "abc123" },
            new() { "e", "def456" },
            new() { "h", "groupid" }
        };
        var (id, pubkey, createdAt, kind, _, content, sig) =
            CreateSignedEvent(kind: 445, content: "encrypted-mls", tags: tags);

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_WithUnicodeContent_PassesVerification()
    {
        var (id, pubkey, createdAt, kind, tags, content, sig) =
            CreateSignedEvent(content: "Hello 日本語 émojis 🎉 and \"quotes\" and \\backslashes\\");

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_Kind1059GiftWrap_PassesVerification()
    {
        // Gift wrap outer signature is verified (inner rumor is unsigned)
        var (id, pubkey, createdAt, kind, tags, content, sig) =
            CreateSignedEvent(kind: 1059, content: "encrypted-seal",
                tags: new List<List<string>> { new() { "p", "recipientpubkey" } });

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    #endregion

    #region Forged events rejected

    [Fact]
    public void TamperedContent_IsRejected()
    {
        // Attacker intercepts event and changes content
        var (id, pubkey, createdAt, kind, tags, _, sig) = CreateSignedEvent(content: "original");

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, "tampered-content", sig));
    }

    [Fact]
    public void TamperedTags_IsRejected()
    {
        // Attacker adds a tag to redirect the event
        var originalTags = new List<List<string>> { new() { "p", "alice" } };
        var (id, pubkey, createdAt, kind, _, content, sig) =
            CreateSignedEvent(tags: originalTags);

        var tamperedTags = new List<List<string>>
        {
            new() { "p", "alice" },
            new() { "p", "attacker" }  // injected tag
        };

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tamperedTags, content, sig));
    }

    [Fact]
    public void ForgedSignature_IsRejected()
    {
        // Attacker creates event with wrong private key's signature
        var (_, attackerPub, _, _) = _nostrService.GenerateKeyPair();
        var (victimPriv, _, _, _) = _nostrService.GenerateKeyPair();
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent(victimPriv);

        // Sign with a different key (attacker forging victim's event)
        var (attackerPriv2, _, _, _) = _nostrService.GenerateKeyPair();
        var fakeIdBytes = Convert.FromHexString(id);
        var fakeSigBytes = NostrService.SignSchnorr(fakeIdBytes, Convert.FromHexString(attackerPriv2));
        var fakeSig = Convert.ToHexString(fakeSigBytes).ToLowerInvariant();

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, fakeSig));
    }

    [Fact]
    public void MismatchedPubkey_IsRejected()
    {
        // Attacker claims event is from victim but signs with own key
        var (attackerPriv, _, _, _) = _nostrService.GenerateKeyPair();
        var (_, victimPub, _, _) = _nostrService.GenerateKeyPair();
        var (id, _, createdAt, kind, tags, content, sig) = CreateSignedEvent(attackerPriv);

        // Replace pubkey with victim's — ID won't match anymore
        Assert.False(NostrService.VerifyEventSignature(
            id, victimPub, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedEventId_IsRejected()
    {
        // Attacker changes the event ID
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var fakeId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        Assert.False(NostrService.VerifyEventSignature(
            fakeId, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedTimestamp_IsRejected()
    {
        // Attacker changes created_at to backdate the event
        var (id, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt - 3600, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedKind_IsRejected()
    {
        // Attacker changes kind (e.g., from kind 1 text note to kind 445 group message)
        var (id, pubkey, createdAt, _, tags, content, sig) = CreateSignedEvent(kind: 1);

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, 445, tags, content, sig));
    }

    #endregion

    #region Malformed input rejected

    [Fact]
    public void EmptySignature_IsRejected()
    {
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent();
        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, ""));
    }

    [Fact]
    public void ShortEventId_IsRejected()
    {
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        Assert.False(NostrService.VerifyEventSignature(
            "abcd", pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexEventId_IsRejected()
    {
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var nonHex = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        Assert.False(NostrService.VerifyEventSignature(
            nonHex, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexPubkey_IsRejected()
    {
        var (id, _, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var nonHex = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        Assert.False(NostrService.VerifyEventSignature(
            id, nonHex, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexSignature_IsRejected()
    {
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent();
        var nonHex = new string('z', 128);
        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, nonHex));
    }

    #endregion
}
