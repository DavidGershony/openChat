using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;

namespace OpenChat.Core.Crypto;

/// <summary>
/// NIP-44 v2 encryption/decryption: ECDH + HKDF + ChaCha20 + HMAC-SHA256.
/// </summary>
public static class Nip44
{
    /// <summary>
    /// Encrypt plaintext using NIP-44 v2.
    /// Returns base64-encoded payload: version(1) || nonce(32) || ciphertext || hmac(32).
    /// </summary>
    public static string Encrypt(string plaintext, string senderPrivateKeyHex, string recipientPublicKeyHex)
    {
        var conversationKey = ComputeConversationKey(senderPrivateKeyHex, recipientPublicKeyHex);
        return EncryptWithConversationKey(plaintext, conversationKey);
    }

    /// <summary>
    /// Decrypt NIP-44 v2 base64 payload.
    /// </summary>
    public static string Decrypt(string base64Payload, string recipientPrivateKeyHex, string senderPublicKeyHex)
    {
        var conversationKey = ComputeConversationKey(recipientPrivateKeyHex, senderPublicKeyHex);
        return DecryptWithConversationKey(base64Payload, conversationKey);
    }

    private static byte[] ComputeConversationKey(string privateKeyHex, string publicKeyHex)
    {
        var sharedSecret = ComputeEcdhSharedSecret(privateKeyHex, publicKeyHex);
        var salt = Encoding.UTF8.GetBytes("nip44-v2");
        return HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, salt);
    }

    private static string EncryptWithConversationKey(string plaintext, byte[] conversationKey)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Pad plaintext (NIP-44 padding: 2-byte big-endian length + plaintext + zero-padding to next power of 2, min 32)
        var paddedLen = CalcPaddedLen(plaintextBytes.Length);
        var padded = new byte[2 + paddedLen];
        padded[0] = (byte)(plaintextBytes.Length >> 8);
        padded[1] = (byte)(plaintextBytes.Length & 0xFF);
        Buffer.BlockCopy(plaintextBytes, 0, padded, 2, plaintextBytes.Length);
        // Rest is already zero-filled

        // Random nonce
        var nonce = RandomNumberGenerator.GetBytes(32);

        // Message keys = HKDF-Expand(prk=conversation_key, info=nonce, L=76)
        var messageKeys = HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, 76, nonce);
        var chachaKey = messageKeys[..32];
        var chachaNonce = messageKeys[32..44];
        var hmacKey = messageKeys[44..76];

        // Encrypt with ChaCha20
        var ciphertext = ChaCha20Transform(chachaKey, chachaNonce, padded);

        // HMAC-SHA256(hmac_key, nonce || ciphertext)
        using var hmacSha256 = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var macInput = new byte[nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, macInput, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, macInput, nonce.Length, ciphertext.Length);
        var mac = hmacSha256.ComputeHash(macInput);

        // Assemble: version(1) || nonce(32) || ciphertext || hmac(32)
        var payload = new byte[1 + 32 + ciphertext.Length + 32];
        payload[0] = 2; // version
        Buffer.BlockCopy(nonce, 0, payload, 1, 32);
        Buffer.BlockCopy(ciphertext, 0, payload, 33, ciphertext.Length);
        Buffer.BlockCopy(mac, 0, payload, 33 + ciphertext.Length, 32);

        return Convert.ToBase64String(payload);
    }

    private static string DecryptWithConversationKey(string base64Payload, byte[] conversationKey)
    {
        var payload = Convert.FromBase64String(base64Payload);

        if (payload.Length < 99)
            throw new FormatException("NIP-44 payload too short");

        if (payload[0] != 2)
            throw new FormatException($"Unsupported NIP-44 version: {payload[0]}");

        var nonce = payload[1..33];
        var ciphertext = payload[33..^32];
        var mac = payload[^32..];

        // Message keys = HKDF-Expand(prk=conversation_key, info=nonce, L=76)
        var messageKeys = HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, 76, nonce);
        var chachaKey = messageKeys[..32];
        var chachaNonce = messageKeys[32..44];
        var hmacKey = messageKeys[44..76];

        // Verify HMAC
        using var hmacSha256 = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var macInput = new byte[nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, macInput, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, macInput, nonce.Length, ciphertext.Length);
        var expectedMac = hmacSha256.ComputeHash(macInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
            throw new CryptographicException("NIP-44 HMAC verification failed");

        // Decrypt with ChaCha20
        var padded = ChaCha20Transform(chachaKey, chachaNonce, ciphertext);

        // Unpad: first 2 bytes are big-endian plaintext length
        var plaintextLen = (padded[0] << 8) | padded[1];
        if (plaintextLen < 1 || plaintextLen > padded.Length - 2)
            throw new FormatException($"Invalid NIP-44 padding length: {plaintextLen}");

        return Encoding.UTF8.GetString(padded, 2, plaintextLen);
    }

    /// <summary>
    /// NIP-44 padding: round up to next power of 2 with minimum of 32.
    /// </summary>
    private static int CalcPaddedLen(int unpaddedLen)
    {
        if (unpaddedLen <= 0) return 32;
        if (unpaddedLen <= 32) return 32;

        // Next power of 2
        var nextPow2 = 1;
        while (nextPow2 < unpaddedLen) nextPow2 <<= 1;
        return nextPow2;
    }

    /// <summary>
    /// ECDH shared secret: x-coordinate of privkey * pubkey.
    /// </summary>
    internal static byte[] ComputeEcdhSharedSecret(string privateKeyHex, string publicKeyHex)
    {
        var privBytes = Convert.FromHexString(privateKeyHex);
        var pubBytes = Convert.FromHexString(publicKeyHex);

        if (!ECPrivKey.TryCreate(privBytes, out var ecPrivKey))
            throw new CryptographicException("Invalid private key");

        // Construct compressed public key (02 prefix + x-coordinate)
        var compressedPub = new byte[33];
        compressedPub[0] = 0x02;
        Array.Copy(pubBytes, 0, compressedPub, 1, 32);

        if (!ECPubKey.TryCreate(compressedPub, null, out _, out var ecPubKey))
            throw new CryptographicException("Invalid public key");

        var sharedPoint = ecPubKey.GetSharedPubkey(ecPrivKey);
        var sharedCompressed = new byte[33];
        sharedPoint.WriteToSpan(true, sharedCompressed, out _);

        var sharedSecret = new byte[32];
        Array.Copy(sharedCompressed, 1, sharedSecret, 0, 32);
        return sharedSecret;
    }

    /// <summary>
    /// ChaCha20 stream cipher (RFC 8439).
    /// </summary>
    internal static byte[] ChaCha20Transform(byte[] key, byte[] nonce, byte[] input)
    {
        var output = new byte[input.Length];
        var state = new uint[16];
        var working = new uint[16];

        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);
        state[12] = 0;
        for (int i = 0; i < 3; i++)
            state[13 + i] = BitConverter.ToUInt32(nonce, i * 4);

        int offset = 0;
        while (offset < input.Length)
        {
            Array.Copy(state, working, 16);
            for (int i = 0; i < 10; i++)
            {
                QR(working, 0, 4, 8, 12); QR(working, 1, 5, 9, 13);
                QR(working, 2, 6, 10, 14); QR(working, 3, 7, 11, 15);
                QR(working, 0, 5, 10, 15); QR(working, 1, 6, 11, 12);
                QR(working, 2, 7, 8, 13); QR(working, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++)
                working[i] += state[i];

            var keystream = new byte[64];
            Buffer.BlockCopy(working, 0, keystream, 0, 64);

            int blockLen = Math.Min(64, input.Length - offset);
            for (int i = 0; i < blockLen; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);

            offset += 64;
            state[12]++;
        }

        return output;

        static void QR(uint[] s, int a, int b, int c, int d)
        {
            s[a] += s[b]; s[d] ^= s[a]; s[d] = (s[d] << 16) | (s[d] >> 16);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = (s[b] << 12) | (s[b] >> 20);
            s[a] += s[b]; s[d] ^= s[a]; s[d] = (s[d] << 8) | (s[d] >> 24);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = (s[b] << 7) | (s[b] >> 25);
        }
    }
}
