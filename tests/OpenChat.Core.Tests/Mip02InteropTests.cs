using System.Text;
using MarmotCs.Protocol.Mip02;
using MarmotCs.Protocol.Nip44;
using MarmotCs.Protocol.Nip59;
using NBitcoin.Secp256k1;
using OpenChat.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-02 compliance tests verifying Welcome event creation, NIP-59 gift wrapping,
/// and NIP-44 encryption/decryption.
///
/// NIP-44 crypto interop with Rust is verified by 103+ official test vectors
/// in MarmotCs.Protocol.Tests.Nip44VectorTests.
///
/// Full NIP-59 interop (C# wrap → Rust unwrap) requires a live relay and
/// marmot-web-chat; those are covered by relay integration tests.
/// </summary>
public class Mip02InteropTests
{
    private readonly ITestOutputHelper _output;

    public Mip02InteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static (byte[] privKey, byte[] pubKey) GenerateKeyPair()
    {
        var ctx = Context.Instance;
        var privBytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        ECPrivKey? ecPriv;
        do
        {
            rng.GetBytes(privBytes);
        } while (!ECPrivKey.TryCreate(privBytes, ctx, out ecPriv));

        var ecPub = ecPriv!.CreateXOnlyPubKey();
        var pubBytes = new byte[32];
        ecPub.WriteToSpan(pubBytes);
        return (privBytes, pubBytes);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Full NIP-59 pipeline — build kind 444 → seal → unseal → parse
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies the complete NIP-59 gift wrap pipeline for MIP-02 Welcome events:
    /// build kind 444 content/tags → GiftWrap.SealContent → GiftWrap.UnsealContent → parse.
    /// </summary>
    [Fact]
    public void FullNip59Pipeline_WelcomeEvent_RoundTrips()
    {
        var (senderPriv, senderPub) = GenerateKeyPair();
        var (recipientPriv, recipientPub) = GenerateKeyPair();

        // Simulate MLS Welcome bytes
        byte[] welcomeBytes = new byte[512];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(welcomeBytes);

        string keyPackageEventId = "abc123def456789012345678901234567890123456789012345678901234abcd";
        string[] relays = new[] { "wss://relay.example.com", "wss://relay2.example.com" };

        // Step 1: Build kind 444 content and tags
        var (content, tags) = WelcomeEventBuilder.BuildWelcomeEvent(welcomeBytes, keyPackageEventId, relays);
        _output.WriteLine($"Built kind 444: content={content.Length} chars, {tags.Length} tags");

        // Verify content is base64-encoded Welcome
        var decodedContent = Convert.FromBase64String(content);
        Assert.Equal(welcomeBytes, decodedContent);

        // Step 2: Seal the content (simulating NIP-59 seal layer)
        byte[] contentBytes = Encoding.UTF8.GetBytes(content);
        byte[] sealedContent = GiftWrap.SealContent(contentBytes, senderPriv, recipientPub);
        _output.WriteLine($"Sealed: {sealedContent.Length} bytes");

        // Step 3: Unseal (simulating NIP-59 unwrap)
        byte[] unsealedContent = GiftWrap.UnsealContent(sealedContent, recipientPriv, senderPub);
        string recoveredContent = Encoding.UTF8.GetString(unsealedContent);
        Assert.Equal(content, recoveredContent);

        // Step 4: Parse the recovered kind 444 event
        var (parsedWelcome, parsedKpId, parsedRelays) =
            WelcomeEventParser.ParseWelcomeEvent(recoveredContent, tags);

        Assert.Equal(welcomeBytes, parsedWelcome);
        Assert.Equal(keyPackageEventId, parsedKpId);
        Assert.Equal(relays, parsedRelays);
        _output.WriteLine("Full NIP-59 pipeline roundtrip successful");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: NIP-44 conversation key commutativity
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that NIP-44 conversation key derivation is commutative:
    /// DeriveConversationKey(privA, pubB) == DeriveConversationKey(privB, pubA).
    /// This is essential for NIP-59 wrapping/unwrapping to work.
    /// </summary>
    [Fact]
    public void Nip44ConversationKey_IsCommutative()
    {
        var (privA, pubA) = GenerateKeyPair();
        var (privB, pubB) = GenerateKeyPair();

        byte[] keyAB = Nip44Encryption.DeriveConversationKey(privA, pubB);
        byte[] keyBA = Nip44Encryption.DeriveConversationKey(privB, pubA);

        Assert.Equal(keyAB, keyBA);
        _output.WriteLine($"Conversation key: {Convert.ToHexString(keyAB).ToLowerInvariant()[..32]}...");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: Double-sealed content (simulates full NIP-59 3-layer wrap)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the full 3-layer NIP-59 gift wrap:
    /// 1. Seal rumor (sender → recipient) = kind 13 seal content
    /// 2. Seal seal (ephemeral → recipient) = kind 1059 gift wrap content
    /// 3. Unseal both layers to recover original
    /// </summary>
    [Fact]
    public void ThreeLayerNip59_SealUnseal_RoundTrips()
    {
        var (senderPriv, senderPub) = GenerateKeyPair();
        var (recipientPriv, recipientPub) = GenerateKeyPair();
        var (ephemeralPriv, ephemeralPub) = GenerateKeyPair();

        // Original rumor content (kind 444 Welcome)
        byte[] rumorContent = Encoding.UTF8.GetBytes(
            "{\"kind\":444,\"content\":\"" + Convert.ToBase64String(new byte[64]) + "\"}");

        // Layer 1: Sender seals rumor for recipient (kind 13 seal)
        byte[] sealContent = GiftWrap.SealContent(rumorContent, senderPriv, recipientPub);
        _output.WriteLine($"Layer 1 (seal): {sealContent.Length} bytes");

        // Layer 2: Ephemeral key seals the seal for recipient (kind 1059 gift wrap)
        byte[] giftWrapContent = GiftWrap.SealContent(sealContent, ephemeralPriv, recipientPub);
        _output.WriteLine($"Layer 2 (gift wrap): {giftWrapContent.Length} bytes");

        // Recipient unwraps layer 2 (gift wrap → seal)
        byte[] recoveredSeal = GiftWrap.UnsealContent(giftWrapContent, recipientPriv, ephemeralPub);
        Assert.Equal(sealContent, recoveredSeal);

        // Recipient unwraps layer 1 (seal → rumor)
        byte[] recoveredRumor = GiftWrap.UnsealContent(recoveredSeal, recipientPriv, senderPub);
        Assert.Equal(rumorContent, recoveredRumor);

        _output.WriteLine("3-layer NIP-59 roundtrip successful");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: Welcome event encoding tag validation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the encoding tag must be "base64" per MIP-02.
    /// </summary>
    [Fact]
    public void WelcomeEvent_WrongEncoding_Throws()
    {
        string content = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        string[][] tags = new[]
        {
            new[] { "e", "abc123" },
            new[] { "encoding", "hex" } // Wrong encoding
        };

        Assert.Throws<FormatException>(() =>
            WelcomeEventParser.ParseWelcomeEvent(content, tags));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 5: Welcome event relays tag structure
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that WelcomeEventBuilder correctly includes multiple relays
    /// in a single "relays" tag per MIP-02.
    /// </summary>
    [Fact]
    public void WelcomeEvent_MultipleRelays_CorrectTagStructure()
    {
        byte[] welcomeBytes = new byte[] { 1, 2, 3 };
        string[] relays = new[] { "wss://relay1.com", "wss://relay2.com", "wss://relay3.com" };

        var (content, tags) = WelcomeEventBuilder.BuildWelcomeEvent(
            welcomeBytes, "event123", relays);

        // Find the relays tag
        var relaysTag = tags.First(t => t[0] == "relays");

        // relays tag should be ["relays", url1, url2, url3]
        Assert.Equal(4, relaysTag.Length); // "relays" + 3 URLs
        Assert.Equal("wss://relay1.com", relaysTag[1]);
        Assert.Equal("wss://relay2.com", relaysTag[2]);
        Assert.Equal("wss://relay3.com", relaysTag[3]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 6: GiftWrap with MLS Welcome-sized content
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies GiftWrap handles realistic MLS Welcome message sizes (~2-8 KB).
    /// </summary>
    [Theory]
    [InlineData(256)]   // Small Welcome
    [InlineData(2048)]  // Typical Welcome
    [InlineData(8192)]  // Large Welcome (multiple members)
    public void GiftWrap_RealisticWelcomeSizes_RoundTrips(int size)
    {
        var (senderPriv, senderPub) = GenerateKeyPair();
        var (recipientPriv, recipientPub) = GenerateKeyPair();

        byte[] welcomeBytes = new byte[size];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(welcomeBytes);

        // Base64-encode like MIP-02 does
        string base64Content = Convert.ToBase64String(welcomeBytes);
        byte[] contentBytes = Encoding.UTF8.GetBytes(base64Content);

        byte[] sealed_ = GiftWrap.SealContent(contentBytes, senderPriv, recipientPub);
        byte[] unsealed = GiftWrap.UnsealContent(sealed_, recipientPriv, senderPub);

        string recoveredBase64 = Encoding.UTF8.GetString(unsealed);
        byte[] recoveredWelcome = Convert.FromBase64String(recoveredBase64);

        Assert.Equal(welcomeBytes, recoveredWelcome);
        _output.WriteLine($"Welcome size {size} bytes → sealed {sealed_.Length} bytes → roundtrip OK");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 7: NIP-44 encrypt/decrypt consistency with GiftWrap
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that GiftWrap.SealContent output can also be decrypted
    /// directly via Nip44Encryption.Decrypt (since GiftWrap wraps NIP-44).
    /// </summary>
    [Fact]
    public void GiftWrap_OutputIsValidNip44Payload()
    {
        var (senderPriv, senderPub) = GenerateKeyPair();
        var (recipientPriv, recipientPub) = GenerateKeyPair();

        byte[] content = Encoding.UTF8.GetBytes("test payload");
        byte[] sealed_ = GiftWrap.SealContent(content, senderPriv, recipientPub);

        // GiftWrap output is UTF-8 encoded NIP-44 payload
        string nip44Payload = Encoding.UTF8.GetString(sealed_);

        // Decrypt directly via NIP-44
        byte[] convKey = Nip44Encryption.DeriveConversationKey(recipientPriv, senderPub);
        string decryptedBase64 = Nip44Encryption.Decrypt(nip44Payload, convKey);
        byte[] decrypted = Convert.FromBase64String(decryptedBase64);

        Assert.Equal(content, decrypted);
        _output.WriteLine("GiftWrap output is valid NIP-44 — cross-implementation compatible");
    }
}
