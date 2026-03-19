using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Tests for secure key storage: ISecureStorage implementations and
/// StorageService integration with encrypted fields.
/// </summary>
public class SecureStorageTests : IDisposable
{
    private readonly List<string> _dbPaths = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    private StorageService CreateStorageService(ISecureStorage? secureStorage = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_sectest_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        return new StorageService(dbPath, secureStorage);
    }

    // ===================================================================
    // Mock ISecureStorage for platform-independent tests
    // ===================================================================

    /// <summary>
    /// Simple XOR-based mock that simulates encrypt/decrypt without OS dependencies.
    /// </summary>
    private class MockSecureStorage : ISecureStorage
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
                    return data; // Not encrypted

            var encrypted = new byte[data.Length - MagicPrefix.Length];
            Array.Copy(data, MagicPrefix.Length, encrypted, 0, encrypted.Length);

            var decrypted = new byte[encrypted.Length];
            for (int i = 0; i < encrypted.Length; i++)
                decrypted[i] = (byte)(encrypted[i] ^ XorKey);
            return decrypted;
        }
    }

    // ===================================================================
    // ISecureStorage round-trip tests
    // ===================================================================

    [Fact]
    public void MockSecureStorage_ProtectUnprotect_RoundTrips()
    {
        var storage = new MockSecureStorage();
        var original = Encoding.UTF8.GetBytes("my-secret-private-key-hex-value");

        var encrypted = storage.Protect(original);
        var decrypted = storage.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void MockSecureStorage_ProtectedData_DiffersFromPlaintext()
    {
        var storage = new MockSecureStorage();
        var original = Encoding.UTF8.GetBytes("secret-key-material");

        var encrypted = storage.Protect(original);

        // Encrypted output must differ from input
        Assert.NotEqual(original, encrypted);
        // Must be longer (has prefix)
        Assert.True(encrypted.Length > original.Length);
    }

    [Fact]
    public void MockSecureStorage_UnprotectPlaintext_ReturnsAsIs()
    {
        var storage = new MockSecureStorage();
        var plaintext = Encoding.UTF8.GetBytes("not-encrypted-data");

        // Data without magic prefix should pass through unchanged
        var result = storage.Unprotect(plaintext);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void MockSecureStorage_EmptyData_RoundTrips()
    {
        var storage = new MockSecureStorage();
        var empty = Array.Empty<byte>();

        var encrypted = storage.Protect(empty);
        var decrypted = storage.Unprotect(encrypted);

        Assert.Equal(empty, decrypted);
    }

    // ===================================================================
    // Windows DPAPI tests (only run on Windows)
    // ===================================================================

    [SkippableFact]
    public void DesktopSecureStorage_ProtectUnprotect_RoundTrips()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI only available on Windows");

        var storage = new OpenChat.UI.Services.DesktopSecureStorage();
        var original = Encoding.UTF8.GetBytes("nostr-private-key-e1f21901bb0990cd36430316");

        var encrypted = storage.Protect(original);
        var decrypted = storage.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    [SkippableFact]
    public void DesktopSecureStorage_ProtectedData_DiffersFromPlaintext()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI only available on Windows");

        var storage = new OpenChat.UI.Services.DesktopSecureStorage();
        var original = Encoding.UTF8.GetBytes("secret-signing-key-material");

        var encrypted = storage.Protect(original);

        Assert.NotEqual(original, encrypted);
        Assert.True(encrypted.Length > original.Length);
        // Must start with magic prefix
        Assert.Equal(0xEE, encrypted[0]);
        Assert.Equal(0xCC, encrypted[1]);
        Assert.Equal(0x01, encrypted[2]);
        Assert.Equal(0x00, encrypted[3]);
    }

    [SkippableFact]
    public void DesktopSecureStorage_UnprotectPlaintext_ReturnsAsIs()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI only available on Windows");

        var storage = new OpenChat.UI.Services.DesktopSecureStorage();
        var plaintext = Encoding.UTF8.GetBytes("unencrypted-legacy-data");

        var result = storage.Unprotect(plaintext);

        Assert.Equal(plaintext, result);
    }

    [SkippableFact]
    public void DesktopSecureStorage_LargeBlob_RoundTrips()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI only available on Windows");

        var storage = new OpenChat.UI.Services.DesktopSecureStorage();
        // Simulate MLS state blob (~1500 bytes typical)
        var original = new byte[2000];
        new Random(42).NextBytes(original);

        var encrypted = storage.Protect(original);
        var decrypted = storage.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    // ===================================================================
    // StorageService integration with secure storage
    // ===================================================================

    [Fact]
    public async Task StorageService_WithSecureStorage_ProtectsPrivateKeyOnSave()
    {
        var secureStorage = new MockSecureStorage();
        var storageService = CreateStorageService(secureStorage);
        await storageService.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            PrivateKeyHex = "deadbeef01234567deadbeef01234567deadbeef01234567deadbeef01234567",
            Nsec = "nsec1testsecretkey",
            DisplayName = "Test User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        await storageService.SaveCurrentUserAsync(user);
        var loaded = await storageService.GetCurrentUserAsync();

        Assert.NotNull(loaded);
        Assert.Equal(user.PrivateKeyHex, loaded!.PrivateKeyHex);
        Assert.Equal(user.Nsec, loaded.Nsec);
    }

    [Fact]
    public async Task StorageService_WithSecureStorage_ProtectsSignerFields()
    {
        var secureStorage = new MockSecureStorage();
        var storageService = CreateStorageService(secureStorage);
        await storageService.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            DisplayName = "Amber User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow,
            SignerRelayUrl = "wss://relay.nsec.app",
            SignerRemotePubKey = "abcd1234abcd1234",
            SignerSecret = "supersecret123",
            SignerLocalPrivateKeyHex = "cafebabe01234567cafebabe01234567cafebabe01234567cafebabe01234567",
            SignerLocalPublicKeyHex = "pubkey1234"
        };

        await storageService.SaveCurrentUserAsync(user);
        var loaded = await storageService.GetCurrentUserAsync();

        Assert.NotNull(loaded);
        Assert.Equal(user.SignerSecret, loaded!.SignerSecret);
        Assert.Equal(user.SignerLocalPrivateKeyHex, loaded.SignerLocalPrivateKeyHex);
        // Non-sensitive fields should be stored as-is
        Assert.Equal(user.SignerRelayUrl, loaded.SignerRelayUrl);
        Assert.Equal(user.SignerRemotePubKey, loaded.SignerRemotePubKey);
        Assert.Equal(user.SignerLocalPublicKeyHex, loaded.SignerLocalPublicKeyHex);
    }

    [Fact]
    public async Task StorageService_WithSecureStorage_ProtectsMlsState()
    {
        var secureStorage = new MockSecureStorage();
        var storageService = CreateStorageService(secureStorage);
        await storageService.InitializeAsync();

        var groupId = "testgroup123";
        var stateData = new byte[500];
        new Random(42).NextBytes(stateData);

        await storageService.SaveMlsStateAsync(groupId, stateData);
        var loaded = await storageService.GetMlsStateAsync(groupId);

        Assert.NotNull(loaded);
        Assert.Equal(stateData, loaded);
    }

    [Fact]
    public async Task StorageService_WithSecureStorage_RawDbValueIsEncrypted()
    {
        var secureStorage = new MockSecureStorage();
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_sectest_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storageService = new StorageService(dbPath, secureStorage);
        await storageService.InitializeAsync();

        var privateKey = "deadbeef01234567deadbeef01234567deadbeef01234567deadbeef01234567";
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            PrivateKeyHex = privateKey,
            DisplayName = "Test",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        await storageService.SaveCurrentUserAsync(user);

        // Read raw value from DB — it should NOT be the plaintext private key
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PrivateKeyHex FROM Users WHERE IsCurrentUser = 1";
        var rawValue = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(rawValue);
        Assert.NotEqual(privateKey, rawValue); // Must NOT be plaintext
    }

    [Fact]
    public async Task StorageService_WithoutSecureStorage_ThrowsOnPrivateKeySave()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_sectest_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storageService = new StorageService(dbPath); // No secure storage
        await storageService.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            PrivateKeyHex = "deadbeef01234567deadbeef01234567deadbeef01234567deadbeef01234567",
            DisplayName = "Test",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        // Must refuse to store private keys without encryption
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storageService.SaveCurrentUserAsync(user));
    }

    [Fact]
    public async Task StorageService_WithoutSecureStorage_AllowsNullPrivateKey()
    {
        // Amber users have no private key — should work without secure storage
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_sectest_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storageService = new StorageService(dbPath); // No secure storage
        await storageService.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            PrivateKeyHex = null,
            Nsec = null,
            DisplayName = "Amber User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        // No private key = no encryption needed, should not throw
        await storageService.SaveCurrentUserAsync(user);
        var loaded = await storageService.GetCurrentUserAsync();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.PrivateKeyHex);
    }

    [Fact]
    public async Task StorageService_WithSecureStorage_SignerSecretsNotPlaintextInDb()
    {
        var secureStorage = new MockSecureStorage();
        var dbPath = Path.Combine(Path.GetTempPath(), $"openchat_sectest_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storageService = new StorageService(dbPath, secureStorage);
        await storageService.InitializeAsync();

        var signerSecret = "supersecret123";
        var signerPrivKey = "cafebabe01234567cafebabe01234567cafebabe01234567cafebabe01234567";
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            DisplayName = "Amber User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow,
            SignerSecret = signerSecret,
            SignerLocalPrivateKeyHex = signerPrivKey
        };

        await storageService.SaveCurrentUserAsync(user);

        // Read raw values from DB — they must NOT be plaintext
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SignerSecret, SignerLocalPrivateKeyHex FROM Users WHERE IsCurrentUser = 1";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var rawSecret = reader.IsDBNull(0) ? null : reader.GetString(0);
        var rawPrivKey = reader.IsDBNull(1) ? null : reader.GetString(1);

        Assert.NotNull(rawSecret);
        Assert.NotNull(rawPrivKey);
        Assert.NotEqual(signerSecret, rawSecret); // Must NOT be plaintext
        Assert.NotEqual(signerPrivKey, rawPrivKey); // Must NOT be plaintext
    }

    [Fact]
    public async Task StorageService_WithSecureStorage_NullFieldsStayNull()
    {
        var secureStorage = new MockSecureStorage();
        var storageService = CreateStorageService(secureStorage);
        await storageService.InitializeAsync();

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "e1f21901bb0990cd36430316aa8a07ad0398fd20feeaaa1bf17434075c69abf1",
            Npub = "npub1test",
            PrivateKeyHex = null, // Amber user — no private key
            Nsec = null,
            DisplayName = "Amber User",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };

        await storageService.SaveCurrentUserAsync(user);
        var loaded = await storageService.GetCurrentUserAsync();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.PrivateKeyHex);
        Assert.Null(loaded.Nsec);
    }
}
