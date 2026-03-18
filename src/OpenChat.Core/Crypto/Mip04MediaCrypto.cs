using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace OpenChat.Core.Crypto;

/// <summary>
/// MIP-04 media encryption/decryption.
/// Derives per-file keys from the MLS exporter secret using HKDF-Expand,
/// then decrypts with ChaCha20-Poly1305 using per-file AAD.
/// </summary>
public static class Mip04MediaCrypto
{
    private static readonly ILogger _logger = LoggingConfiguration.CreateLogger<object>();

    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagSizeBits = 128;
    private const string VersionPrefix = "mip04-v2";

    /// <summary>
    /// Derives a per-file encryption key from the MLS exporter secret.
    /// context = "mip04-v2" || 0x00 || sha256_bytes || 0x00 || canonical_mime_bytes || 0x00 || filename_bytes || 0x00 || "key"
    /// key = HKDF-Expand(SHA256, exporterSecret, context, 32)
    /// </summary>
    public static byte[] DeriveMediaEncryptionKey(byte[] exporterSecret, string sha256Hex, string mimeType, string filename)
    {
        ArgumentNullException.ThrowIfNull(exporterSecret);
        if (exporterSecret.Length != KeyLength)
            throw new ArgumentException($"Exporter secret must be {KeyLength} bytes, got {exporterSecret.Length}.", nameof(exporterSecret));
        if (string.IsNullOrEmpty(sha256Hex))
            throw new ArgumentException("SHA-256 hash is required.", nameof(sha256Hex));
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("Filename is required.", nameof(filename));

        var canonicalMime = CanonicalizeMimeType(mimeType);
        var sha256Bytes = Convert.FromHexString(sha256Hex);
        var mimeBytes = Encoding.UTF8.GetBytes(canonicalMime);
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var keyLabel = Encoding.UTF8.GetBytes("key");
        var versionBytes = Encoding.UTF8.GetBytes(VersionPrefix);

        // Build context: "mip04-v2" || 0x00 || sha256_bytes || 0x00 || mime_bytes || 0x00 || filename_bytes || 0x00 || "key"
        var contextLength = versionBytes.Length + 1 + sha256Bytes.Length + 1 + mimeBytes.Length + 1 + filenameBytes.Length + 1 + keyLabel.Length;
        var context = new byte[contextLength];
        var offset = 0;

        Buffer.BlockCopy(versionBytes, 0, context, offset, versionBytes.Length);
        offset += versionBytes.Length;
        context[offset++] = 0x00;

        Buffer.BlockCopy(sha256Bytes, 0, context, offset, sha256Bytes.Length);
        offset += sha256Bytes.Length;
        context[offset++] = 0x00;

        Buffer.BlockCopy(mimeBytes, 0, context, offset, mimeBytes.Length);
        offset += mimeBytes.Length;
        context[offset++] = 0x00;

        Buffer.BlockCopy(filenameBytes, 0, context, offset, filenameBytes.Length);
        offset += filenameBytes.Length;
        context[offset++] = 0x00;

        Buffer.BlockCopy(keyLabel, 0, context, offset, keyLabel.Length);

        var key = HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, KeyLength, context);

        _logger.LogDebug("DeriveMediaEncryptionKey: sha256={Sha256}, mime={Mime}, filename={Filename}, contextLen={Len}",
            sha256Hex[..Math.Min(16, sha256Hex.Length)], canonicalMime, filename, contextLength);

        return key;
    }

    /// <summary>
    /// Decrypts a MIP-04 encrypted media file.
    /// AAD = "mip04-v2" || 0x00 || sha256_bytes || 0x00 || mime_bytes || 0x00 || filename_bytes
    /// Verifies SHA-256 integrity after decryption.
    /// </summary>
    public static byte[] DecryptMediaFile(byte[] encrypted, byte[] fileKey, string sha256Hex, string mimeType, string filename, string nonceHex)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(fileKey);
        if (fileKey.Length != KeyLength)
            throw new ArgumentException($"File key must be {KeyLength} bytes.", nameof(fileKey));
        if (string.IsNullOrEmpty(nonceHex))
            throw new ArgumentException("Nonce is required.", nameof(nonceHex));
        if (string.IsNullOrEmpty(sha256Hex))
            throw new ArgumentException("SHA-256 hash is required.", nameof(sha256Hex));

        var nonce = Convert.FromHexString(nonceHex);
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"Nonce must be {NonceLength} bytes (24 hex chars), got {nonce.Length} bytes.", nameof(nonceHex));

        var canonicalMime = CanonicalizeMimeType(mimeType);
        var sha256Bytes = Convert.FromHexString(sha256Hex);
        var mimeBytes = Encoding.UTF8.GetBytes(canonicalMime);
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var versionBytes = Encoding.UTF8.GetBytes(VersionPrefix);

        // Build AAD: "mip04-v2" || 0x00 || sha256_bytes || 0x00 || mime_bytes || 0x00 || filename_bytes
        var aadLength = versionBytes.Length + 1 + sha256Bytes.Length + 1 + mimeBytes.Length + 1 + filenameBytes.Length;
        var aad = new byte[aadLength];
        var offset = 0;

        Buffer.BlockCopy(versionBytes, 0, aad, offset, versionBytes.Length);
        offset += versionBytes.Length;
        aad[offset++] = 0x00;

        Buffer.BlockCopy(sha256Bytes, 0, aad, offset, sha256Bytes.Length);
        offset += sha256Bytes.Length;
        aad[offset++] = 0x00;

        Buffer.BlockCopy(mimeBytes, 0, aad, offset, mimeBytes.Length);
        offset += mimeBytes.Length;
        aad[offset++] = 0x00;

        Buffer.BlockCopy(filenameBytes, 0, aad, offset, filenameBytes.Length);

        _logger.LogDebug("DecryptMediaFile: encrypted={Len} bytes, nonceLen={NonceLen}, aadLen={AadLen}",
            encrypted.Length, nonce.Length, aad.Length);

        // ChaCha20-Poly1305 decrypt
        var cipher = new BcChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(fileKey), TagSizeBits, nonce, aad));

        var plaintext = new byte[cipher.GetOutputSize(encrypted.Length)];
        var len = cipher.ProcessBytes(encrypted, 0, encrypted.Length, plaintext, 0);
        len += cipher.DoFinal(plaintext, len);

        // Trim to actual length (GetOutputSize may overestimate)
        if (len < plaintext.Length)
        {
            var trimmed = new byte[len];
            Buffer.BlockCopy(plaintext, 0, trimmed, 0, len);
            plaintext = trimmed;
        }

        // Verify SHA-256 integrity
        var computedHash = SHA256.HashData(plaintext);
        var computedHashHex = Convert.ToHexString(computedHash).ToLowerInvariant();
        var expectedHashHex = sha256Hex.ToLowerInvariant();

        if (computedHashHex != expectedHashHex)
        {
            _logger.LogError("DecryptMediaFile: SHA-256 mismatch! Expected={Expected}, computed={Computed}",
                expectedHashHex[..Math.Min(16, expectedHashHex.Length)],
                computedHashHex[..Math.Min(16, computedHashHex.Length)]);
            throw new CryptographicException(
                $"SHA-256 integrity check failed. File may be corrupted or tampered with. " +
                $"Expected: {expectedHashHex[..16]}..., got: {computedHashHex[..16]}...");
        }

        _logger.LogInformation("DecryptMediaFile: successfully decrypted {Len} bytes, SHA-256 verified", plaintext.Length);
        return plaintext;
    }

    /// <summary>
    /// Canonicalizes a MIME type: lowercase, strip parameters after semicolon.
    /// </summary>
    public static string CanonicalizeMimeType(string mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
            return "application/octet-stream";

        var semicolonIndex = mimeType.IndexOf(';');
        var result = semicolonIndex >= 0 ? mimeType[..semicolonIndex] : mimeType;
        return result.Trim().ToLowerInvariant();
    }
}
