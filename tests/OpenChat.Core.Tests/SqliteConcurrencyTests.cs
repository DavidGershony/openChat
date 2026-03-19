using Microsoft.Data.Sqlite;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Core.Tests.TestHelpers;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Proves that concurrent database operations don't throw "database is locked"
/// or corrupt data. WAL mode + write serialization prevent these issues.
/// </summary>
public class SqliteConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StorageService _storageService;

    public SqliteConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openchat_concurrency_{Guid.NewGuid()}.db");
        _storageService = new StorageService(_dbPath, new MockSecureStorage());
        _storageService.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { }
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotThrowDatabaseLocked()
    {
        // Launch 20 concurrent save operations
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var chat = new Chat
            {
                Id = $"chat-{i}",
                Name = $"Chat {i}",
                Type = ChatType.Group,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };
            await _storageService.SaveChatAsync(chat);

            var message = new Message
            {
                Id = $"msg-{i}",
                ChatId = chat.Id,
                SenderPublicKey = "sender123",
                Content = $"Message {i}",
                Type = MessageType.Text,
                Status = MessageStatus.Sent,
                Timestamp = DateTime.UtcNow
            };
            await _storageService.SaveMessageAsync(message);
        });

        // Should not throw "database is locked"
        await Task.WhenAll(tasks);

        // Verify all data was saved
        var chats = (await _storageService.GetAllChatsAsync()).ToList();
        Assert.Equal(20, chats.Count);
    }

    [Fact]
    public async Task ConcurrentReadAndWrite_DoNotConflict()
    {
        // Seed some data
        for (int i = 0; i < 5; i++)
        {
            await _storageService.SaveChatAsync(new Chat
            {
                Id = $"seed-{i}",
                Name = $"Seed {i}",
                Type = ChatType.Group,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            });
        }

        // Concurrent reads + writes
        var writeTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await _storageService.SaveChatAsync(new Chat
            {
                Id = $"concurrent-{i}",
                Name = $"Concurrent {i}",
                Type = ChatType.Group,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            });
        });

        var readTasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var chats = await _storageService.GetAllChatsAsync();
            Assert.True(chats.Any()); // Should always find data
        });

        await Task.WhenAll(writeTasks.Concat(readTasks));

        var finalChats = (await _storageService.GetAllChatsAsync()).ToList();
        Assert.Equal(15, finalChats.Count); // 5 seed + 10 concurrent
    }

    [Fact]
    public async Task ConcurrentMlsStateWrites_Serialized()
    {
        var groupId = "test-group";

        // Write MLS state 10 times concurrently — should not corrupt
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var state = new byte[100];
            new Random(i).NextBytes(state);
            await _storageService.SaveMlsStateAsync(groupId, state);
        });

        await Task.WhenAll(tasks);

        // Should have one valid state (last write wins, but no corruption)
        var result = await _storageService.GetMlsStateAsync(groupId);
        Assert.NotNull(result);
        Assert.Equal(100, result!.Length);
    }
}
