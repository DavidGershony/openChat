using Microsoft.Data.Sqlite;
using Scramble.Core.Configuration;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Core.Tests.TestHelpers;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for the Android share target core logic:
/// - ShareChatLoader: loading and grouping chats for the share UI
/// - ShareAccountResolver: resolving which account to pre-select
/// - SharePayload: data model for shared content
/// </summary>
public class ShareTargetTests : IAsyncLifetime
{
    private readonly string _testDbPath;
    private readonly StorageService _storageService;
    private readonly string _testRootDir;

    public ShareTargetTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"scramble_share_test_{Guid.NewGuid()}.db");
        _storageService = new StorageService(_testDbPath, new MockSecureStorage());
        _testRootDir = Path.Combine(Path.GetTempPath(), $"scramble_share_root_{Guid.NewGuid()}");
    }

    public async Task InitializeAsync()
    {
        await _storageService.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try { if (File.Exists(_testDbPath)) File.Delete(_testDbPath); } catch (IOException) { }
        try { if (Directory.Exists(_testRootDir)) Directory.Delete(_testRootDir, true); } catch (IOException) { }

        return Task.CompletedTask;
    }

    // ───────────────────────────────────────────────
    // Test 1: ShareChatLoader — chat loading & grouping
    // ───────────────────────────────────────────────

    [Fact]
    public async Task LoadChatsForShare_ReturnsBotChatsFirst_ThenRegularChats()
    {
        // Arrange: set up profile DB for a fake account
        var pubKeyHex = "aa".PadRight(64, 'a');
        var dbPath = SetUpProfileDb(pubKeyHex);

        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var botChat = new Chat
        {
            Id = "bot-1", Name = "My Device", Type = ChatType.Bot,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddHours(-1)
        };
        var dmChat = new Chat
        {
            Id = "dm-1", Name = "Alice", Type = ChatType.DirectMessage,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddMinutes(-30)
        };
        var groupChat = new Chat
        {
            Id = "group-1", Name = "Team", Type = ChatType.Group,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var archivedChat = new Chat
        {
            Id = "archived-1", Name = "Old Chat", Type = ChatType.DirectMessage,
            IsArchived = true,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow
        };

        await storage.SaveChatAsync(botChat);
        await storage.SaveChatAsync(dmChat);
        await storage.SaveChatAsync(groupChat);
        await storage.SaveChatAsync(archivedChat);

        // Act
        var result = await ShareChatLoader.LoadAsync(pubKeyHex);

        // Assert
        Assert.Equal(1, result.DeviceChats.Count);
        Assert.Equal("bot-1", result.DeviceChats[0].Id);

        Assert.Equal(2, result.RegularChats.Count);
        // Group chat is more recent (-10m) than DM (-30m)
        Assert.Equal("group-1", result.RegularChats[0].Id);
        Assert.Equal("dm-1", result.RegularChats[1].Id);

        // Archived chat excluded
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task LoadChatsForShare_EmptyDatabase_ReturnsEmpty()
    {
        var pubKeyHex = "bb".PadRight(64, 'b');
        var dbPath = SetUpProfileDb(pubKeyHex);

        // Initialize DB but don't add chats
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var result = await ShareChatLoader.LoadAsync(pubKeyHex);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.DeviceChats);
        Assert.Empty(result.RegularChats);
    }

    [Fact]
    public async Task LoadChatsForShare_OnlyBots_ReturnsBotsOnly()
    {
        var pubKeyHex = "cc".PadRight(64, 'c');
        var dbPath = SetUpProfileDb(pubKeyHex);

        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var bot1 = new Chat
        {
            Id = "bot-older", Name = "Bot A", Type = ChatType.Bot,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddHours(-2)
        };
        var bot2 = new Chat
        {
            Id = "bot-newer", Name = "Bot B", Type = ChatType.Bot,
            CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow.AddHours(-1)
        };

        await storage.SaveChatAsync(bot1);
        await storage.SaveChatAsync(bot2);

        var result = await ShareChatLoader.LoadAsync(pubKeyHex);

        Assert.Equal(2, result.DeviceChats.Count);
        Assert.Equal("bot-newer", result.DeviceChats[0].Id);
        Assert.Equal("bot-older", result.DeviceChats[1].Id);
        Assert.Empty(result.RegularChats);
    }

    [Fact]
    public async Task LoadChatsForShare_NonexistentDb_ReturnsEmpty()
    {
        // Account with no profile DB on disk
        var pubKeyHex = "dd".PadRight(64, 'd');
        // Don't create the DB

        var result = await ShareChatLoader.LoadAsync(pubKeyHex);

        Assert.Equal(0, result.TotalCount);
    }

    // ───────────────────────────────────────────────
    // Test 2: ShareAccountResolver — account resolution
    // ───────────────────────────────────────────────

    [Fact]
    public void ResolveShareAccount_ReturnsActiveAccount()
    {
        SetUpAccountRegistry();

        var entryA = new AccountEntry
        {
            PublicKeyHex = "aa".PadRight(64, 'a'),
            Npub = "npub1aaa", DisplayName = "Alice",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow.AddHours(-2)
        };
        var entryB = new AccountEntry
        {
            PublicKeyHex = "bb".PadRight(64, 'b'),
            Npub = "npub1bbb", DisplayName = "Bob",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow.AddHours(-1)
        };
        var entryC = new AccountEntry
        {
            PublicKeyHex = "cc".PadRight(64, 'c'),
            Npub = "npub1ccc", DisplayName = "Carol",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow
        };

        AccountRegistryService.AddOrUpdateAccount(entryA);
        AccountRegistryService.AddOrUpdateAccount(entryB);
        AccountRegistryService.AddOrUpdateAccount(entryC);
        AccountRegistryService.SetActiveAccount(entryA.PublicKeyHex);

        var resolved = ShareAccountResolver.Resolve();

        Assert.NotNull(resolved);
        Assert.Equal(entryA.PublicKeyHex, resolved!.PublicKeyHex);
    }

    [Fact]
    public void ResolveShareAccount_NoActiveAccount_ReturnsFirstByLastActive()
    {
        SetUpAccountRegistry();

        var entryOld = new AccountEntry
        {
            PublicKeyHex = "aa".PadRight(64, 'a'),
            Npub = "npub1old", DisplayName = "Old",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow.AddDays(-7)
        };
        var entryRecent = new AccountEntry
        {
            PublicKeyHex = "bb".PadRight(64, 'b'),
            Npub = "npub1recent", DisplayName = "Recent",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow.AddHours(-1)
        };

        AccountRegistryService.AddOrUpdateAccount(entryOld);
        AccountRegistryService.AddOrUpdateAccount(entryRecent);
        // Don't set active — simulate post-logout state
        AccountRegistryService.ClearActiveAccount();

        var resolved = ShareAccountResolver.Resolve();

        Assert.NotNull(resolved);
        Assert.Equal(entryRecent.PublicKeyHex, resolved!.PublicKeyHex);
    }

    [Fact]
    public void ResolveShareAccount_SingleAccount_ReturnsThatAccount()
    {
        SetUpAccountRegistry();

        var entry = new AccountEntry
        {
            PublicKeyHex = "aa".PadRight(64, 'a'),
            Npub = "npub1only", DisplayName = "Solo",
            AddedAt = DateTime.UtcNow, LastActiveAt = DateTime.UtcNow
        };
        AccountRegistryService.AddOrUpdateAccount(entry);
        AccountRegistryService.SetActiveAccount(entry.PublicKeyHex);

        var resolved = ShareAccountResolver.Resolve();

        Assert.NotNull(resolved);
        Assert.Equal(entry.PublicKeyHex, resolved!.PublicKeyHex);
    }

    [Fact]
    public void ResolveShareAccount_NoAccounts_ReturnsNull()
    {
        SetUpAccountRegistry();
        // Empty registry — no accounts added

        var resolved = ShareAccountResolver.Resolve();

        Assert.Null(resolved);
    }

    // ───────────────────────────────────────────────
    // Test 3: SharePayload — data model
    // ───────────────────────────────────────────────

    [Fact]
    public void SharePayload_TextOnly_CorrectType()
    {
        var payload = new SharePayload
        {
            Text = "https://example.com",
            MimeType = "text/plain"
        };

        Assert.True(payload.IsText);
        Assert.False(payload.IsMedia);
        Assert.False(payload.IsMultiple);
        Assert.Equal("https://example.com", payload.Text);
    }

    [Fact]
    public void SharePayload_ImageUri_CorrectType()
    {
        var payload = new SharePayload
        {
            Uris = new[] { "content://media/external/images/123" },
            MimeType = "image/jpeg"
        };

        Assert.False(payload.IsText);
        Assert.True(payload.IsMedia);
        Assert.False(payload.IsMultiple);
        Assert.Equal("image/jpeg", payload.MimeType);
    }

    [Fact]
    public void SharePayload_GenericFile_CorrectType()
    {
        var payload = new SharePayload
        {
            Uris = new[] { "content://downloads/doc.pdf" },
            MimeType = "application/pdf"
        };

        Assert.True(payload.IsMedia);
        Assert.False(payload.IsText);
    }

    [Fact]
    public void SharePayload_MultipleUris_CorrectCount()
    {
        var payload = new SharePayload
        {
            Uris = new[] {
                "content://media/1",
                "content://media/2",
                "content://media/3"
            },
            MimeType = "image/*"
        };

        Assert.Equal(3, payload.Uris.Count);
        Assert.True(payload.IsMultiple);
        Assert.True(payload.IsMedia);
        Assert.False(payload.IsText);
    }

    [Fact]
    public void SharePayload_NoTextNoUris_NeitherTextNorMedia()
    {
        var payload = new SharePayload();

        Assert.False(payload.IsText);
        Assert.False(payload.IsMedia);
        Assert.False(payload.IsMultiple);
    }

    // ───────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────

    /// <summary>
    /// Creates the profile directory and returns the DB path for ShareChatLoader to find.
    /// Sets RootDataDirectory to a temp path so tests are isolated.
    /// </summary>
    private string SetUpProfileDb(string pubKeyHex)
    {
        ProfileConfiguration.SetRootDataDirectory(_testRootDir);
        var profileName = ProfileConfiguration.DeriveProfileName(pubKeyHex);
        var profileDir = Path.Combine(_testRootDir, "profiles", profileName);
        Directory.CreateDirectory(profileDir);
        return Path.Combine(profileDir, "scramble.db");
    }

    /// <summary>
    /// Resets AccountRegistryService to a clean state pointing at the temp directory.
    /// </summary>
    private void SetUpAccountRegistry()
    {
        ProfileConfiguration.SetRootDataDirectory(_testRootDir);
        Directory.CreateDirectory(_testRootDir);
        AccountRegistryService.Reload();
    }
}
