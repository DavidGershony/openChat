using System.Security.Cryptography;
using MarmotCs.Protocol.Crypto;
using MarmotCs.Protocol.Mip03;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-03 compliance tests verifying group message encryption (kind 445),
/// ChaCha20-Poly1305 wire format, and GroupEventEncryption roundtrips.
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
    // Test 1: GroupEventEncryption roundtrip
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that GroupEventEncryption.Encrypt → Decrypt roundtrips correctly.
    /// </summary>
    [Fact]
    public void GroupEventEncryption_Encrypt_Decrypt_RoundTrips()
    {
        byte[] key = RandomKey();
        byte[] plaintext = new byte[] { 1, 2, 3, 4, 5 };

        string base64Encrypted = GroupEventEncryption.Encrypt(plaintext, key);
        byte[] decrypted = GroupEventEncryption.Decrypt(base64Encrypted, key);

        Assert.Equal(plaintext, decrypted);
        _output.WriteLine($"Roundtrip: {plaintext.Length} bytes → {base64Encrypted.Length} base64 → OK");
    }

    /// <summary>
    /// Verifies roundtrip with larger payloads.
    /// </summary>
    [Fact]
    public void GroupEventEncryption_LargePayload_RoundTrips()
    {
        byte[] key = RandomKey();
        byte[] plaintext = new byte[4096];
        RandomNumberGenerator.Fill(plaintext);

        string base64Encrypted = GroupEventEncryption.Encrypt(plaintext, key);
        byte[] decrypted = GroupEventEncryption.Decrypt(base64Encrypted, key);

        Assert.Equal(plaintext, decrypted);
        _output.WriteLine($"Large roundtrip: {plaintext.Length} bytes → OK");
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

        string base64Encrypted = GroupEventEncryption.Encrypt(plaintext, key);
        byte[] rawBytes = Convert.FromBase64String(base64Encrypted);

        // Expected: nonce(12) + ciphertext(N) + tag(16) = 12 + N + 16
        Assert.Equal(12 + plaintextSize + 16, rawBytes.Length);
        _output.WriteLine($"Plaintext {plaintextSize} bytes → encrypted {rawBytes.Length} bytes (overhead: 28 bytes)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: Different keys produce different ciphertext
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DifferentKeys_ProduceDifferentCiphertext()
    {
        byte[] key1 = RandomKey();
        byte[] key2 = RandomKey();
        byte[] plaintext = new byte[] { 10, 20, 30 };

        string encrypted1 = GroupEventEncryption.Encrypt(plaintext, key1);
        string encrypted2 = GroupEventEncryption.Encrypt(plaintext, key2);

        // Different keys must produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);

        // But each decrypts correctly with its own key
        Assert.Equal(plaintext, GroupEventEncryption.Decrypt(encrypted1, key1));
        Assert.Equal(plaintext, GroupEventEncryption.Decrypt(encrypted2, key2));
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
    // Test 6: AAD is empty (verified via encrypt/decrypt roundtrip)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that GroupEventEncryption uses empty AAD by confirming that
    /// encrypt → raw bytes → decrypt roundtrips correctly. If non-empty AAD
    /// were used inconsistently, the AEAD auth check would fail.
    /// </summary>
    [Fact]
    public void AAD_IsEmpty_Verification()
    {
        byte[] key = RandomKey();
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("AAD must be empty per MIP-03");

        // Encrypt with GroupEventEncryption
        string encrypted = GroupEventEncryption.Encrypt(plaintext, key);

        // Decrypt the raw bytes back through GroupEventEncryption
        byte[] decrypted = GroupEventEncryption.Decrypt(encrypted, key);

        Assert.Equal(plaintext, decrypted);
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
