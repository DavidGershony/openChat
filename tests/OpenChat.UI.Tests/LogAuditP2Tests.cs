using OpenChat.Core.Services;
using OpenChat.UI.Tests.TestHelpers;
using Xunit;

namespace OpenChat.UI.Tests;

/// <summary>
/// Tests for P2 caching fixes from the 2026-04-02 log audit.
/// </summary>
public class LogAuditP2Tests : IDisposable
{
    private readonly string _dbPath;
    private readonly StorageService _storage;

    public LogAuditP2Tests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openchat_p2_{Guid.NewGuid()}.db");
        _storage = new StorageService(_dbPath, new MockSecureStorage());
        _storage.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetSettingAsync_ReturnsCachedValue_OnSecondCall()
    {
        await _storage.SaveSettingAsync("test_key", "test_value");

        var first = await _storage.GetSettingAsync("test_key");
        var second = await _storage.GetSettingAsync("test_key");

        Assert.Equal("test_value", first);
        Assert.Equal("test_value", second);
    }

    [Fact]
    public async Task SaveSettingAsync_InvalidatesCache()
    {
        await _storage.SaveSettingAsync("key1", "old_value");
        var cached = await _storage.GetSettingAsync("key1");
        Assert.Equal("old_value", cached);

        await _storage.SaveSettingAsync("key1", "new_value");
        var updated = await _storage.GetSettingAsync("key1");
        Assert.Equal("new_value", updated);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsCachedUser_OnSecondCall()
    {
        var user = new OpenChat.Core.Models.User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "aa".PadLeft(64, 'a'),
            PrivateKeyHex = "bb".PadLeft(64, 'b'),
            Npub = "npub1testtest",
            DisplayName = "Cache Test",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await _storage.SaveCurrentUserAsync(user);

        var first = await _storage.GetCurrentUserAsync();
        var second = await _storage.GetCurrentUserAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.PublicKeyHex, second!.PublicKeyHex);
    }

    [Fact]
    public async Task ClearCurrentUserAsync_InvalidatesUserCache()
    {
        var user = new OpenChat.Core.Models.User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = "cc".PadLeft(64, 'c'),
            PrivateKeyHex = "dd".PadLeft(64, 'd'),
            Npub = "npub1cleartest",
            DisplayName = "Clear Test",
            IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await _storage.SaveCurrentUserAsync(user);

        var before = await _storage.GetCurrentUserAsync();
        Assert.NotNull(before);

        await _storage.ClearCurrentUserAsync();
        var after = await _storage.GetCurrentUserAsync();
        Assert.Null(after);
    }
}
