using OpenChat.Core.Services;

namespace OpenChat.Core.Tests.TestHelpers;

/// <summary>
/// Simple XOR-based mock that simulates encrypt/decrypt without OS dependencies.
/// Used across all test files that need StorageService with secure storage.
/// </summary>
public class MockSecureStorage : ISecureStorage
{
    private static readonly byte[] MagicPrefix = { 0xEE, 0xCC, 0x01, 0x00 };
    private const byte XorKey = 0x42;

    public byte[] Protect(byte[] data)
    {
        var encrypted = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            encrypted[i] = (byte)(data[i] ^ XorKey);

        var result = new byte[MagicPrefix.Length + encrypted.Length];
        MagicPrefix.CopyTo(result, 0);
        encrypted.CopyTo(result, MagicPrefix.Length);
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < MagicPrefix.Length)
            return data;

        for (int i = 0; i < MagicPrefix.Length; i++)
            if (data[i] != MagicPrefix[i])
                return data; // Not encrypted — backward compat

        var encrypted = new byte[data.Length - MagicPrefix.Length];
        Array.Copy(data, MagicPrefix.Length, encrypted, 0, encrypted.Length);

        var decrypted = new byte[encrypted.Length];
        for (int i = 0; i < encrypted.Length; i++)
            decrypted[i] = (byte)(encrypted[i] ^ XorKey);
        return decrypted;
    }
}
