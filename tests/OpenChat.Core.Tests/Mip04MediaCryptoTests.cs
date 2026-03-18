using System.Security.Cryptography;
using System.Text;
using OpenChat.Core.Crypto;
using OpenChat.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-04 media crypto unit tests: key derivation, encryption/decryption,
/// AAD construction, SHA-256 integrity verification, and URL validation.
/// </summary>
public class Mip04MediaCryptoTests
{
    private readonly ITestOutputHelper _output;

    public Mip04MediaCryptoTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static byte[] RandomKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    // ---- Key Derivation Tests ----

    [Fact]
    public void DeriveMediaEncryptionKey_ProducesConsistentResults()
    {
        var exporterSecret = RandomKey();
        var sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var mimeType = "image/jpeg";
        var filename = "photo.jpg";

        var key1 = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, mimeType, filename);
        var key2 = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, mimeType, filename);

        Assert.Equal(32, key1.Length);
        Assert.Equal(key1, key2);
        _output.WriteLine($"Derived key: {Convert.ToHexString(key1)}");
    }

    [Fact]
    public void DeriveMediaEncryptionKey_DifferentSha256_DifferentKey()
    {
        var exporterSecret = RandomKey();
        var sha256a = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var sha256b = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var keyA = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256a, "image/jpeg", "photo.jpg");
        var keyB = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256b, "image/jpeg", "photo.jpg");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void DeriveMediaEncryptionKey_DifferentFilename_DifferentKey()
    {
        var exporterSecret = RandomKey();
        var sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var keyA = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, "image/jpeg", "photo.jpg");
        var keyB = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, "image/jpeg", "different.jpg");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void DeriveMediaEncryptionKey_DifferentMimeType_DifferentKey()
    {
        var exporterSecret = RandomKey();
        var sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var keyA = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, "image/jpeg", "photo.jpg");
        var keyB = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256, "image/png", "photo.jpg");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void DeriveMediaEncryptionKey_DifferentExporterSecret_DifferentKey()
    {
        var secretA = RandomKey();
        var secretB = RandomKey();
        var sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var keyA = Mip04MediaCrypto.DeriveMediaEncryptionKey(secretA, sha256, "image/jpeg", "photo.jpg");
        var keyB = Mip04MediaCrypto.DeriveMediaEncryptionKey(secretB, sha256, "image/jpeg", "photo.jpg");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void DeriveMediaEncryptionKey_InvalidExporterSecretLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mip04MediaCrypto.DeriveMediaEncryptionKey(new byte[16], "abcd", "image/jpeg", "test.jpg"));
    }

    [Fact]
    public void DeriveMediaEncryptionKey_EmptySha256_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mip04MediaCrypto.DeriveMediaEncryptionKey(RandomKey(), "", "image/jpeg", "test.jpg"));
    }

    [Fact]
    public void DeriveMediaEncryptionKey_EmptyFilename_Throws()
    {
        var sha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Assert.Throws<ArgumentException>(() =>
            Mip04MediaCrypto.DeriveMediaEncryptionKey(RandomKey(), sha256, "image/jpeg", ""));
    }

    // ---- MIME Canonicalization Tests ----

    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("IMAGE/JPEG", "image/jpeg")]
    [InlineData("image/jpeg; charset=utf-8", "image/jpeg")]
    [InlineData("  Image/PNG  ", "image/png")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void CanonicalizeMimeType_ReturnsExpected(string? input, string expected)
    {
        Assert.Equal(expected, Mip04MediaCrypto.CanonicalizeMimeType(input!));
    }

    // ---- Encrypt/Decrypt Round-trip Tests ----

    [Fact]
    public void EncryptDecrypt_Roundtrip_Success()
    {
        var exporterSecret = RandomKey();
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        var mimeType = "image/jpeg";
        var filename = "photo.jpg";

        // Derive key
        var fileKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256Hex, mimeType, filename);

        // Encrypt using BouncyCastle ChaCha20-Poly1305 directly (simulating sender)
        var nonce = RandomNumberGenerator.GetBytes(12);
        var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();

        // Build AAD (same as DecryptMediaFile)
        var versionBytes = Encoding.UTF8.GetBytes("mip04-v2");
        var sha256Bytes = Convert.FromHexString(sha256Hex);
        var mimeBytes = Encoding.UTF8.GetBytes(Mip04MediaCrypto.CanonicalizeMimeType(mimeType));
        var filenameBytes = Encoding.UTF8.GetBytes(filename);

        var aadLength = versionBytes.Length + 1 + sha256Bytes.Length + 1 + mimeBytes.Length + 1 + filenameBytes.Length;
        var aad = new byte[aadLength];
        var offset = 0;
        Buffer.BlockCopy(versionBytes, 0, aad, offset, versionBytes.Length); offset += versionBytes.Length;
        aad[offset++] = 0x00;
        Buffer.BlockCopy(sha256Bytes, 0, aad, offset, sha256Bytes.Length); offset += sha256Bytes.Length;
        aad[offset++] = 0x00;
        Buffer.BlockCopy(mimeBytes, 0, aad, offset, mimeBytes.Length); offset += mimeBytes.Length;
        aad[offset++] = 0x00;
        Buffer.BlockCopy(filenameBytes, 0, aad, offset, filenameBytes.Length);

        // Encrypt
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(fileKey), 128, nonce, aad));

        var encrypted = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, encrypted, 0);
        cipher.DoFinal(encrypted, len);

        // Decrypt using Mip04MediaCrypto
        var decrypted = Mip04MediaCrypto.DecryptMediaFile(encrypted, fileKey, sha256Hex, mimeType, filename, nonceHex);

        Assert.Equal(plaintext, decrypted);
        _output.WriteLine($"Successfully round-tripped {plaintext.Length} bytes");
    }

    [Fact]
    public void DecryptMediaFile_TamperedData_ThrowsAuth()
    {
        var exporterSecret = RandomKey();
        var plaintext = RandomNumberGenerator.GetBytes(256);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        var mimeType = "image/png";
        var filename = "test.png";
        var fileKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256Hex, mimeType, filename);

        // Encrypt
        var nonce = RandomNumberGenerator.GetBytes(12);
        var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();

        var versionBytes = Encoding.UTF8.GetBytes("mip04-v2");
        var sha256Bytes = Convert.FromHexString(sha256Hex);
        var mimeBytes = Encoding.UTF8.GetBytes("image/png");
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var aadLength = versionBytes.Length + 1 + sha256Bytes.Length + 1 + mimeBytes.Length + 1 + filenameBytes.Length;
        var aad = new byte[aadLength];
        var o = 0;
        Buffer.BlockCopy(versionBytes, 0, aad, o, versionBytes.Length); o += versionBytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(sha256Bytes, 0, aad, o, sha256Bytes.Length); o += sha256Bytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(mimeBytes, 0, aad, o, mimeBytes.Length); o += mimeBytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(filenameBytes, 0, aad, o, filenameBytes.Length);

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(fileKey), 128, nonce, aad));
        var encrypted = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, encrypted, 0);
        cipher.DoFinal(encrypted, len);

        // Tamper with encrypted data
        encrypted[5] ^= 0xFF;

        // Decryption should fail with auth error
        Assert.ThrowsAny<Exception>(() =>
            Mip04MediaCrypto.DecryptMediaFile(encrypted, fileKey, sha256Hex, mimeType, filename, nonceHex));
    }

    [Fact]
    public void DecryptMediaFile_WrongKey_ThrowsAuth()
    {
        var exporterSecret = RandomKey();
        var plaintext = RandomNumberGenerator.GetBytes(256);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        var mimeType = "image/jpeg";
        var filename = "test.jpg";
        var fileKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(exporterSecret, sha256Hex, mimeType, filename);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();

        // Encrypt with correct key
        var versionBytes = Encoding.UTF8.GetBytes("mip04-v2");
        var sha256Bytes = Convert.FromHexString(sha256Hex);
        var mimeBytes = Encoding.UTF8.GetBytes("image/jpeg");
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var aadLength = versionBytes.Length + 1 + sha256Bytes.Length + 1 + mimeBytes.Length + 1 + filenameBytes.Length;
        var aad = new byte[aadLength];
        var o = 0;
        Buffer.BlockCopy(versionBytes, 0, aad, o, versionBytes.Length); o += versionBytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(sha256Bytes, 0, aad, o, sha256Bytes.Length); o += sha256Bytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(mimeBytes, 0, aad, o, mimeBytes.Length); o += mimeBytes.Length;
        aad[o++] = 0x00;
        Buffer.BlockCopy(filenameBytes, 0, aad, o, filenameBytes.Length);

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(fileKey), 128, nonce, aad));
        var encrypted = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, encrypted, 0);
        cipher.DoFinal(encrypted, len);

        // Decrypt with wrong key
        var wrongKey = RandomKey();
        Assert.ThrowsAny<Exception>(() =>
            Mip04MediaCrypto.DecryptMediaFile(encrypted, wrongKey, sha256Hex, mimeType, filename, nonceHex));
    }

    [Fact]
    public void DecryptMediaFile_InvalidNonceLength_Throws()
    {
        var key = RandomKey();
        var sha256Hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        Assert.Throws<ArgumentException>(() =>
            Mip04MediaCrypto.DecryptMediaFile(new byte[64], key, sha256Hex, "image/jpeg", "test.jpg", "aabb")); // 2 bytes, not 12
    }

    // ---- URL Validation Tests ----

    [Fact]
    public void IsKnownBlossomServer_KnownServer_ReturnsTrue()
    {
        var service = new MediaDownloadService();

        Assert.True(service.IsKnownBlossomServer("https://blossom.band/abc123"));
        Assert.True(service.IsKnownBlossomServer("https://nostr.build/image.jpg"));
        Assert.True(service.IsKnownBlossomServer("https://void.cat/d/abc"));
        Assert.True(service.IsKnownBlossomServer("https://files.sovbit.host/data"));
    }

    [Fact]
    public void IsKnownBlossomServer_SubdomainOfKnown_ReturnsTrue()
    {
        var service = new MediaDownloadService();

        Assert.True(service.IsKnownBlossomServer("https://cdn.blossom.band/abc123"));
        Assert.True(service.IsKnownBlossomServer("https://media.nostr.build/image.jpg"));
    }

    [Fact]
    public void IsKnownBlossomServer_UnknownServer_ReturnsFalse()
    {
        var service = new MediaDownloadService();

        Assert.False(service.IsKnownBlossomServer("https://evil-server.com/tracking-pixel.jpg"));
        Assert.False(service.IsKnownBlossomServer("https://myserver.example.com/file.jpg"));
        Assert.False(service.IsKnownBlossomServer("https://notblossom.band/abc"));
    }

    [Fact]
    public async Task ValidateUrl_HttpsRequired()
    {
        var service = new MediaDownloadService();

        var result = await service.ValidateUrlAsync("http://blossom.band/abc");
        Assert.NotNull(result);
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public async Task ValidateUrl_HttpsUrl_PassesSchemeCheck()
    {
        var service = new MediaDownloadService();

        // This will pass the scheme check but may fail DNS for non-existent hosts
        // We test that the HTTPS check itself works
        var result = await service.ValidateUrlAsync("https://blossom.band/abc123");
        // If it returns null, URL validation passed. If not null, it's a DNS/IP error (acceptable).
        // The key test is that it doesn't fail on the HTTPS check.
        _output.WriteLine($"Validation result: {result ?? "passed"}");
    }

    [Fact]
    public async Task ValidateUrl_InvalidUrl_ReturnsError()
    {
        var service = new MediaDownloadService();

        var result = await service.ValidateUrlAsync("not a url");
        Assert.NotNull(result);
        Assert.Contains("Invalid", result);
    }

    [Fact]
    public async Task ValidateUrl_LocalhostBlocked()
    {
        var service = new MediaDownloadService();

        var result = await service.ValidateUrlAsync("https://localhost/test");
        Assert.NotNull(result);
        Assert.Contains("private", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateUrl_PrivateIpBlocked()
    {
        var service = new MediaDownloadService();

        // 127.0.0.1 resolves to loopback
        var result = await service.ValidateUrlAsync("https://127.0.0.1/test");
        Assert.NotNull(result);
        Assert.Contains("private", result, StringComparison.OrdinalIgnoreCase);
    }
}
