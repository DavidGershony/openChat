using System.Security.Cryptography;
using MarmotCs.Core;
using MarmotCs.Protocol.Crypto;
using MarmotCs.Protocol.Mip03;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-03 compliance tests verifying group message encryption (kind 445),
/// ChaCha20-Poly1305 wire format, and cross-layer compatibility between
/// MarmotCs.Protocol (GroupEventEncryption) and MarmotCs.Core (Mip03Crypto).
///
/// Full cross-MDK interop (C# ↔ Rust) for kind 445 is tested in
/// EndToEndChatIntegrationTests (requires relay + native DLL).
/// </summary>
public class Mip03InteropTests
{
    private readonly ITestOutputHelper _output;

    public Mip03InteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static byte[] RandomKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Mip03Crypto ↔ GroupEventEncryption compatibility
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that Mip03Crypto.Encrypt (Core layer, raw bytes) produces output
    /// that can be decoded by GroupEventEncryption.Decrypt (Protocol layer, base64).
    /// This tests cross-layer compatibility.
    /// </summary>
    [Fact]
    public void Mip03Crypto_Encrypt_GroupEventEncryption_Decrypt_Compatible()
    {
        byte[] key = RandomKey();
        byte[] plaintext = new byte[] { 1, 2, 3, 4, 5 };

        // Core layer: encrypt to raw bytes (nonce || ciphertext || tag)
        byte[] rawEncrypted = Mip03Crypto.Encrypt(key, plaintext);

        // Protocol layer expects base64(nonce || ciphertext || tag)
        string base64Encrypted = Convert.ToBase64String(rawEncrypted);

        // Protocol layer: decrypt from base64
        byte[] decrypted = GroupEventEncryption.Decrypt(base64Encrypted, key);

        Assert.Equal(plaintext, decrypted);
        _output.WriteLine($"Core→Protocol roundtrip: {plaintext.Length} bytes → {rawEncrypted.Length} raw → {base64Encrypted.Length} base64 → OK");
    }

    /// <summary>
    /// Verifies that GroupEventEncryption.Encrypt (Protocol layer, base64) produces output
    /// that can be decoded by Mip03Crypto.Decrypt (Core layer, raw bytes).
    /// </summary>
    [Fact]
    public void GroupEventEncryption_Encrypt_Mip03Crypto_Decrypt_Compatible()
    {
        byte[] key = RandomKey();
        byte[] plaintext = new byte[] { 10, 20, 30, 40, 50 };

        // Protocol layer: encrypt to base64
        string base64Encrypted = GroupEventEncryption.Encrypt(plaintext, key);

        // Core layer: expects raw bytes
        byte[] rawEncrypted = Convert.FromBase64String(base64Encrypted);
        byte[] decrypted = Mip03Crypto.Decrypt(key, rawEncrypted);

        Assert.Equal(plaintext, decrypted);
        _output.WriteLine($"Protocol→Core roundtrip: {plaintext.Length} bytes → OK");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: Wire format compliance — nonce[12] || ciphertext || tag[16]
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the MIP-03 wire format is nonce[12] || ciphertext || tag[16],
    /// where the total size = 12 + plaintext_length + 16.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void WireFormat_CorrectSize(int plaintextSize)
    {
        byte[] key = RandomKey();
        byte[] plaintext = new byte[plaintextSize];
        RandomNumberGenerator.Fill(plaintext);

        byte[] encrypted = Mip03Crypto.Encrypt(key, plaintext);

        // Expected: nonce(12) + ciphertext(N) + tag(16) = 12 + N + 16
        Assert.Equal(12 + plaintextSize + 16, encrypted.Length);
        _output.WriteLine($"Plaintext {plaintextSize} bytes → encrypted {encrypted.Length} bytes (overhead: 28 bytes)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: Exporter constants match
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that Mip03Crypto exporter constants match MIP-03 spec:
    /// label="marmot", context="group-event", length=32.
    /// </summary>
    [Fact]
    public void ExporterConstants_MatchSpec()
    {
        Assert.Equal("marmot", Mip03Crypto.ExporterLabel);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("group-event"), Mip03Crypto.ExporterContext);
        Assert.Equal(32, Mip03Crypto.ExporterLength);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: CommitRaceResolver determinism
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies CommitRaceResolver produces the same winner regardless of input order.
    /// </summary>
    [Fact]
    public void CommitRaceResolver_OrderIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var commits = new[]
        {
            ("aaaa", now),
            ("bbbb", now.AddSeconds(1)),
            ("cccc", now),
        };

        // Try all permutations
        var perm1 = new[] { commits[0], commits[1], commits[2] };
        var perm2 = new[] { commits[2], commits[1], commits[0] };
        var perm3 = new[] { commits[1], commits[0], commits[2] };

        string winner1 = CommitRaceResolver.ResolveWinner(perm1);
        string winner2 = CommitRaceResolver.ResolveWinner(perm2);
        string winner3 = CommitRaceResolver.ResolveWinner(perm3);

        Assert.Equal(winner1, winner2);
        Assert.Equal(winner2, winner3);
        Assert.Equal("aaaa", winner1); // earliest time + smallest ID
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 5: Full pipeline — build kind 445 → parse
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the full MIP-03 pipeline: MLS message → build kind 445 event
    /// → transmit (content + tags) → parse and decrypt → recover original.
    /// </summary>
    [Fact]
    public void FullPipeline_BuildAndParse_RoundTrips()
    {
        byte[] exporterSecret = RandomKey();
        byte[] mlsMessage = new byte[256];
        RandomNumberGenerator.Fill(mlsMessage);
        byte[] groupId = new byte[32];
        RandomNumberGenerator.Fill(groupId);

        // Sender: build kind 445 event
        var (content, tags) = GroupEventBuilder.BuildGroupEvent(mlsMessage, groupId, exporterSecret);

        // Verify tags
        var hTag = tags.First(t => t[0] == "h");
        Assert.Equal(Convert.ToHexString(groupId).ToLowerInvariant(), hTag[1]);
        var encTag = tags.First(t => t[0] == "encoding");
        Assert.Equal("base64", encTag[1]);

        // Receiver: parse and decrypt
        var (parsedMessage, parsedGroupId) = GroupEventParser.ParseGroupEvent(content, tags, exporterSecret);

        Assert.Equal(mlsMessage, parsedMessage);
        Assert.Equal(groupId, parsedGroupId);
        _output.WriteLine("Full MIP-03 pipeline roundtrip successful");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 6: AAD is empty (not null, not group ID)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that GroupEventEncryption uses empty AAD by cross-decrypting
    /// with Mip03Crypto which also uses empty AAD (no AAD parameter at all,
    /// which defaults to empty in BouncyCastle). If either used non-empty AAD,
    /// the cross-layer decryption would fail with AEAD auth error.
    /// </summary>
    [Fact]
    public void AAD_IsEmpty_CrossLayerVerification()
    {
        byte[] key = RandomKey();
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("AAD must be empty per MIP-03");

        // Encrypt with Protocol layer (explicitly passes Array.Empty<byte>() as AAD)
        string protocolEncrypted = GroupEventEncryption.Encrypt(plaintext, key);
        byte[] rawBytes = Convert.FromBase64String(protocolEncrypted);

        // Decrypt with Core layer (no AAD parameter = empty)
        byte[] coreDecrypted = Mip03Crypto.Decrypt(key, rawBytes);

        Assert.Equal(plaintext, coreDecrypted);

        // And vice versa
        byte[] coreEncrypted = Mip03Crypto.Encrypt(key, plaintext);
        string base64Core = Convert.ToBase64String(coreEncrypted);
        byte[] protocolDecrypted = GroupEventEncryption.Decrypt(base64Core, key);

        Assert.Equal(plaintext, protocolDecrypted);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 7: CommitRaceResolver hex case insensitivity
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that CommitRaceResolver treats hex event IDs as case-insensitive.
    /// This ensures consistent results regardless of hex casing (Nostr convention
    /// is lowercase, but other implementations may use uppercase).
    /// </summary>
    [Fact]
    public void CommitRaceResolver_HexCaseInsensitive()
    {
        var now = DateTimeOffset.UtcNow;

        // "ABCD" and "abcd" should be treated as equal
        var commits = new[]
        {
            ("ABCD", now),
            ("abce", now), // "abce" > "abcd" regardless of case
        };

        string winner = CommitRaceResolver.ResolveWinner(commits);
        Assert.Equal("ABCD", winner); // "abcd" == "ABCD" < "abce"
    }
}
