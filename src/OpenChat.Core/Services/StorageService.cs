using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

public class StorageService : IStorageService
{
    private readonly ILogger<StorageService> _logger;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private bool _initialized;

    public StorageService(string? databasePath = null)
    {
        _logger = LoggingConfiguration.CreateLogger<StorageService>();

        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenChat",
            "openchat.db");

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Created database directory: {Directory}", directory);
        }

        _connectionString = $"Data Source={_databasePath}";
        _logger.LogInformation("StorageService initialized with database: {DatabasePath}", _databasePath);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            _logger.LogDebug("Database already initialized, skipping");
            return;
        }

        _logger.LogInformation("Initializing database schema");

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            _logger.LogDebug("Database connection opened");

            var command = connection.CreateCommand();
            command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id TEXT PRIMARY KEY,
                PublicKeyHex TEXT NOT NULL UNIQUE,
                Npub TEXT,
                PrivateKeyHex TEXT,
                Nsec TEXT,
                DisplayName TEXT,
                Username TEXT,
                AvatarUrl TEXT,
                About TEXT,
                Nip05 TEXT,
                CreatedAt TEXT NOT NULL,
                LastUpdatedAt TEXT,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Chats (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type INTEGER NOT NULL,
                MlsGroupId BLOB,
                MlsEpoch INTEGER NOT NULL DEFAULT 0,
                AvatarUrl TEXT,
                Description TEXT,
                UnreadCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                LastActivityAt TEXT NOT NULL,
                IsMuted INTEGER NOT NULL DEFAULT 0,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                IsArchived INTEGER NOT NULL DEFAULT 0,
                WelcomeNostrEventId TEXT
            );

            CREATE TABLE IF NOT EXISTS ChatParticipants (
                ChatId TEXT NOT NULL,
                PublicKeyHex TEXT NOT NULL,
                PRIMARY KEY (ChatId, PublicKeyHex),
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS ChatRelays (
                ChatId TEXT NOT NULL,
                RelayUrl TEXT NOT NULL,
                PRIMARY KEY (ChatId, RelayUrl),
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id TEXT PRIMARY KEY,
                ChatId TEXT NOT NULL,
                SenderPublicKey TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Content TEXT NOT NULL,
                EncryptedContent TEXT,
                NostrEventId TEXT,
                MlsEpoch INTEGER NOT NULL DEFAULT 0,
                Timestamp TEXT NOT NULL,
                ReceivedAt TEXT,
                Status INTEGER NOT NULL,
                ReplyToMessageId TEXT,
                IsFromCurrentUser INTEGER NOT NULL DEFAULT 0,
                IsEdited INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ChatId) REFERENCES Chats(Id)
            );

            CREATE TABLE IF NOT EXISTS MessageReactions (
                MessageId TEXT NOT NULL,
                Emoji TEXT NOT NULL,
                ReactorPublicKey TEXT NOT NULL,
                PRIMARY KEY (MessageId, Emoji, ReactorPublicKey),
                FOREIGN KEY (MessageId) REFERENCES Messages(Id)
            );

            CREATE TABLE IF NOT EXISTS KeyPackages (
                Id TEXT PRIMARY KEY,
                OwnerPublicKey TEXT NOT NULL,
                Data BLOB NOT NULL,
                NostrEventId TEXT,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                IsUsed INTEGER NOT NULL DEFAULT 0,
                CiphersuiteId INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KeyPackageRelays (
                KeyPackageId TEXT NOT NULL,
                RelayUrl TEXT NOT NULL,
                PRIMARY KEY (KeyPackageId, RelayUrl),
                FOREIGN KEY (KeyPackageId) REFERENCES KeyPackages(Id)
            );

            CREATE TABLE IF NOT EXISTS MlsStates (
                GroupId TEXT PRIMARY KEY,
                State BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PendingInvites (
                Id TEXT PRIMARY KEY,
                SenderPublicKey TEXT NOT NULL,
                GroupId TEXT,
                WelcomeData BLOB NOT NULL,
                KeyPackageEventId TEXT,
                NostrEventId TEXT NOT NULL,
                ReceivedAt TEXT NOT NULL,
                SenderDisplayName TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_PendingInvites_NostrEventId ON PendingInvites(NostrEventId);

            CREATE TABLE IF NOT EXISTS DismissedWelcomeEvents (
                NostrEventId TEXT PRIMARY KEY,
                DismissedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_ChatId ON Messages(ChatId);
            CREATE INDEX IF NOT EXISTS IX_Messages_Timestamp ON Messages(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Messages_NostrEventId ON Messages(NostrEventId);
            CREATE INDEX IF NOT EXISTS IX_Chats_LastActivityAt ON Chats(LastActivityAt);
            CREATE INDEX IF NOT EXISTS IX_KeyPackages_OwnerPublicKey ON KeyPackages(OwnerPublicKey);
        ";

            await command.ExecuteNonQueryAsync();

            // Migration: add WelcomeNostrEventId column for existing databases
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Chats ADD COLUMN WelcomeNostrEventId TEXT";
                await migrate.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }

            _initialized = true;
            _logger.LogInformation("Database schema initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database schema");
            throw;
        }
    }

    public async Task<User?> GetCurrentUserAsync()
    {
        _logger.LogDebug("Retrieving current user from database");

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Users WHERE IsCurrentUser = 1";

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var user = ReadUser(reader);
                _logger.LogInformation("Retrieved current user: {Npub}", user.Npub ?? user.PublicKeyHex[..16]);
                return user;
            }

            _logger.LogDebug("No current user found in database");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve current user");
            throw;
        }
    }

    public async Task SaveCurrentUserAsync(User user)
    {
        _logger.LogInformation("Saving current user: {Npub}", user.Npub ?? user.PublicKeyHex[..16]);
        user.IsCurrentUser = true;
        await SaveUserAsync(user);
    }

    public async Task<User?> GetUserByPublicKeyAsync(string publicKeyHex)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users WHERE PublicKeyHex = @PublicKeyHex";
        command.Parameters.AddWithValue("@PublicKeyHex", publicKeyHex);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadUser(reader);
        }

        return null;
    }

    public async Task SaveUserAsync(User user)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Users
            (Id, PublicKeyHex, Npub, PrivateKeyHex, Nsec, DisplayName, Username, AvatarUrl, About, Nip05, CreatedAt, LastUpdatedAt, IsCurrentUser)
            VALUES
            (@Id, @PublicKeyHex, @Npub, @PrivateKeyHex, @Nsec, @DisplayName, @Username, @AvatarUrl, @About, @Nip05, @CreatedAt, @LastUpdatedAt, @IsCurrentUser)";

        command.Parameters.AddWithValue("@Id", user.Id);
        command.Parameters.AddWithValue("@PublicKeyHex", user.PublicKeyHex);
        command.Parameters.AddWithValue("@Npub", user.Npub ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@PrivateKeyHex", user.PrivateKeyHex ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Nsec", user.Nsec ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@DisplayName", user.DisplayName);
        command.Parameters.AddWithValue("@Username", user.Username ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AvatarUrl", user.AvatarUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@About", user.About ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Nip05", user.Nip05 ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", user.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastUpdatedAt", user.LastUpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@IsCurrentUser", user.IsCurrentUser ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        var users = new List<User>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public async Task<Chat?> GetChatAsync(string chatId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            chat.ParticipantPublicKeys = await GetChatParticipantsAsync(connection, chatId);
            chat.RelayUrls = await GetChatRelaysAsync(connection, chatId);
            return chat;
        }

        return null;
    }

    public async Task<IEnumerable<Chat>> GetAllChatsAsync()
    {
        var chats = new List<Chat>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Chats WHERE IsArchived = 0 ORDER BY IsPinned DESC, LastActivityAt DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chat = ReadChat(reader);
            chat.ParticipantPublicKeys = await GetChatParticipantsAsync(connection, chat.Id);
            chat.RelayUrls = await GetChatRelaysAsync(connection, chat.Id);
            chats.Add(chat);
        }

        return chats;
    }

    public async Task SaveChatAsync(Chat chat)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Chats
            (Id, Name, Type, MlsGroupId, MlsEpoch, AvatarUrl, Description, UnreadCount, CreatedAt, LastActivityAt, IsMuted, IsPinned, IsArchived, WelcomeNostrEventId)
            VALUES
            (@Id, @Name, @Type, @MlsGroupId, @MlsEpoch, @AvatarUrl, @Description, @UnreadCount, @CreatedAt, @LastActivityAt, @IsMuted, @IsPinned, @IsArchived, @WelcomeNostrEventId)";

        command.Parameters.AddWithValue("@Id", chat.Id);
        command.Parameters.AddWithValue("@Name", chat.Name);
        command.Parameters.AddWithValue("@Type", (int)chat.Type);
        command.Parameters.AddWithValue("@MlsGroupId", chat.MlsGroupId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@MlsEpoch", (long)chat.MlsEpoch);
        command.Parameters.AddWithValue("@AvatarUrl", chat.AvatarUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", chat.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@UnreadCount", chat.UnreadCount);
        command.Parameters.AddWithValue("@CreatedAt", chat.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastActivityAt", chat.LastActivityAt.ToString("O"));
        command.Parameters.AddWithValue("@IsMuted", chat.IsMuted ? 1 : 0);
        command.Parameters.AddWithValue("@IsPinned", chat.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("@IsArchived", chat.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("@WelcomeNostrEventId", chat.WelcomeNostrEventId ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();

        // Save participants
        var deleteParticipants = connection.CreateCommand();
        deleteParticipants.CommandText = "DELETE FROM ChatParticipants WHERE ChatId = @ChatId";
        deleteParticipants.Parameters.AddWithValue("@ChatId", chat.Id);
        await deleteParticipants.ExecuteNonQueryAsync();

        foreach (var publicKey in chat.ParticipantPublicKeys)
        {
            var insertParticipant = connection.CreateCommand();
            insertParticipant.CommandText = "INSERT INTO ChatParticipants (ChatId, PublicKeyHex) VALUES (@ChatId, @PublicKeyHex)";
            insertParticipant.Parameters.AddWithValue("@ChatId", chat.Id);
            insertParticipant.Parameters.AddWithValue("@PublicKeyHex", publicKey);
            await insertParticipant.ExecuteNonQueryAsync();
        }

        // Save relays
        var deleteRelays = connection.CreateCommand();
        deleteRelays.CommandText = "DELETE FROM ChatRelays WHERE ChatId = @ChatId";
        deleteRelays.Parameters.AddWithValue("@ChatId", chat.Id);
        await deleteRelays.ExecuteNonQueryAsync();

        foreach (var relayUrl in chat.RelayUrls)
        {
            var insertRelay = connection.CreateCommand();
            insertRelay.CommandText = "INSERT INTO ChatRelays (ChatId, RelayUrl) VALUES (@ChatId, @RelayUrl)";
            insertRelay.Parameters.AddWithValue("@ChatId", chat.Id);
            insertRelay.Parameters.AddWithValue("@RelayUrl", relayUrl);
            await insertRelay.ExecuteNonQueryAsync();
        }
    }

    public async Task DeleteChatAsync(string chatId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM MessageReactions WHERE MessageId IN (SELECT Id FROM Messages WHERE ChatId = @ChatId);
            DELETE FROM Messages WHERE ChatId = @ChatId;
            DELETE FROM ChatParticipants WHERE ChatId = @ChatId;
            DELETE FROM ChatRelays WHERE ChatId = @ChatId;
            DELETE FROM Chats WHERE Id = @ChatId;";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<Message?> GetMessageAsync(string messageId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Messages WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, messageId);
            return message;
        }

        return null;
    }

    public async Task<IEnumerable<Message>> GetMessagesForChatAsync(string chatId, int limit = 50, int offset = 0)
    {
        var messages = new List<Message>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM Messages
            WHERE ChatId = @ChatId AND IsDeleted = 0
            ORDER BY Timestamp DESC
            LIMIT @Limit OFFSET @Offset";
        command.Parameters.AddWithValue("@ChatId", chatId);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var message = ReadMessage(reader);
            message.Reactions = await GetMessageReactionsAsync(connection, message.Id);
            messages.Add(message);
        }

        messages.Reverse();
        return messages;
    }

    public async Task SaveMessageAsync(Message message)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Messages
            (Id, ChatId, SenderPublicKey, Type, Content, EncryptedContent, NostrEventId, MlsEpoch, Timestamp, ReceivedAt, Status, ReplyToMessageId, IsFromCurrentUser, IsEdited, IsDeleted)
            VALUES
            (@Id, @ChatId, @SenderPublicKey, @Type, @Content, @EncryptedContent, @NostrEventId, @MlsEpoch, @Timestamp, @ReceivedAt, @Status, @ReplyToMessageId, @IsFromCurrentUser, @IsEdited, @IsDeleted)";

        command.Parameters.AddWithValue("@Id", message.Id);
        command.Parameters.AddWithValue("@ChatId", message.ChatId);
        command.Parameters.AddWithValue("@SenderPublicKey", message.SenderPublicKey);
        command.Parameters.AddWithValue("@Type", (int)message.Type);
        command.Parameters.AddWithValue("@Content", message.Content);
        command.Parameters.AddWithValue("@EncryptedContent", message.EncryptedContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@NostrEventId", message.NostrEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@MlsEpoch", (long)message.MlsEpoch);
        command.Parameters.AddWithValue("@Timestamp", message.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("@ReceivedAt", message.ReceivedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", (int)message.Status);
        command.Parameters.AddWithValue("@ReplyToMessageId", message.ReplyToMessageId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@IsFromCurrentUser", message.IsFromCurrentUser ? 1 : 0);
        command.Parameters.AddWithValue("@IsEdited", message.IsEdited ? 1 : 0);
        command.Parameters.AddWithValue("@IsDeleted", message.IsDeleted ? 1 : 0);

        await command.ExecuteNonQueryAsync();

        // Save reactions
        var deleteReactions = connection.CreateCommand();
        deleteReactions.CommandText = "DELETE FROM MessageReactions WHERE MessageId = @MessageId";
        deleteReactions.Parameters.AddWithValue("@MessageId", message.Id);
        await deleteReactions.ExecuteNonQueryAsync();

        foreach (var (emoji, reactors) in message.Reactions)
        {
            foreach (var reactor in reactors)
            {
                var insertReaction = connection.CreateCommand();
                insertReaction.CommandText = "INSERT INTO MessageReactions (MessageId, Emoji, ReactorPublicKey) VALUES (@MessageId, @Emoji, @ReactorPublicKey)";
                insertReaction.Parameters.AddWithValue("@MessageId", message.Id);
                insertReaction.Parameters.AddWithValue("@Emoji", emoji);
                insertReaction.Parameters.AddWithValue("@ReactorPublicKey", reactor);
                await insertReaction.ExecuteNonQueryAsync();
            }
        }
    }

    public async Task UpdateMessageStatusAsync(string messageId, MessageStatus status)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Messages SET Status = @Status WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);
        command.Parameters.AddWithValue("@Status", (int)status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteMessageAsync(string messageId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Messages SET IsDeleted = 1, Content = '' WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> MessageExistsByNostrEventIdAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Messages WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    public async Task<KeyPackage?> GetKeyPackageAsync(string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, id);
            return keyPackage;
        }

        return null;
    }

    public async Task<IEnumerable<KeyPackage>> GetUnusedKeyPackagesAsync(string ownerPublicKey)
    {
        var keyPackages = new List<KeyPackage>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM KeyPackages WHERE OwnerPublicKey = @OwnerPublicKey AND IsUsed = 0 AND ExpiresAt > @Now";
        command.Parameters.AddWithValue("@OwnerPublicKey", ownerPublicKey);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var keyPackage = ReadKeyPackage(reader);
            keyPackage.RelayUrls = await GetKeyPackageRelaysAsync(connection, keyPackage.Id);
            keyPackages.Add(keyPackage);
        }

        return keyPackages;
    }

    public async Task SaveKeyPackageAsync(KeyPackage keyPackage)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO KeyPackages
            (Id, OwnerPublicKey, Data, NostrEventId, CreatedAt, ExpiresAt, IsUsed, CiphersuiteId)
            VALUES
            (@Id, @OwnerPublicKey, @Data, @NostrEventId, @CreatedAt, @ExpiresAt, @IsUsed, @CiphersuiteId)";

        command.Parameters.AddWithValue("@Id", keyPackage.Id);
        command.Parameters.AddWithValue("@OwnerPublicKey", keyPackage.OwnerPublicKey);
        command.Parameters.AddWithValue("@Data", keyPackage.Data);
        command.Parameters.AddWithValue("@NostrEventId", keyPackage.NostrEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", keyPackage.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ExpiresAt", keyPackage.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("@IsUsed", keyPackage.IsUsed ? 1 : 0);
        command.Parameters.AddWithValue("@CiphersuiteId", keyPackage.CiphersuiteId);

        await command.ExecuteNonQueryAsync();

        // Save relays
        var deleteRelays = connection.CreateCommand();
        deleteRelays.CommandText = "DELETE FROM KeyPackageRelays WHERE KeyPackageId = @KeyPackageId";
        deleteRelays.Parameters.AddWithValue("@KeyPackageId", keyPackage.Id);
        await deleteRelays.ExecuteNonQueryAsync();

        foreach (var relayUrl in keyPackage.RelayUrls)
        {
            var insertRelay = connection.CreateCommand();
            insertRelay.CommandText = "INSERT INTO KeyPackageRelays (KeyPackageId, RelayUrl) VALUES (@KeyPackageId, @RelayUrl)";
            insertRelay.Parameters.AddWithValue("@KeyPackageId", keyPackage.Id);
            insertRelay.Parameters.AddWithValue("@RelayUrl", relayUrl);
            await insertRelay.ExecuteNonQueryAsync();
        }
    }

    public async Task MarkKeyPackageUsedAsync(string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE KeyPackages SET IsUsed = 1 WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveMlsStateAsync(string groupId, byte[] state)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO MlsStates (GroupId, State) VALUES (@GroupId, @State)";
        command.Parameters.AddWithValue("@GroupId", groupId);
        command.Parameters.AddWithValue("@State", state);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<byte[]?> GetMlsStateAsync(string groupId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM MlsStates WHERE GroupId = @GroupId";
        command.Parameters.AddWithValue("@GroupId", groupId);

        var result = await command.ExecuteScalarAsync();
        return result as byte[];
    }

    public async Task DeleteMlsStateAsync(string groupId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MlsStates WHERE GroupId = @GroupId";
        command.Parameters.AddWithValue("@GroupId", groupId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UndismissWelcomeEventAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DismissedWelcomeEvents WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync()
    {
        var invites = new List<PendingInvite>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PendingInvites ORDER BY ReceivedAt DESC";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            invites.Add(ReadPendingInvite(reader));
        }

        return invites;
    }

    public async Task SavePendingInviteAsync(PendingInvite invite)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO PendingInvites
            (Id, SenderPublicKey, GroupId, WelcomeData, KeyPackageEventId, NostrEventId, ReceivedAt, SenderDisplayName)
            VALUES
            (@Id, @SenderPublicKey, @GroupId, @WelcomeData, @KeyPackageEventId, @NostrEventId, @ReceivedAt, @SenderDisplayName)";

        command.Parameters.AddWithValue("@Id", invite.Id);
        command.Parameters.AddWithValue("@SenderPublicKey", invite.SenderPublicKey);
        command.Parameters.AddWithValue("@GroupId", invite.GroupId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@WelcomeData", invite.WelcomeData);
        command.Parameters.AddWithValue("@KeyPackageEventId", invite.KeyPackageEventId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@NostrEventId", invite.NostrEventId);
        command.Parameters.AddWithValue("@ReceivedAt", invite.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("@SenderDisplayName", invite.SenderDisplayName ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeletePendingInviteAsync(string inviteId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingInvites WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", inviteId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DismissWelcomeEventAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO DismissedWelcomeEvents (NostrEventId, DismissedAt)
            VALUES (@NostrEventId, @DismissedAt)";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);
        command.Parameters.AddWithValue("@DismissedAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsWelcomeEventDismissedAsync(string nostrEventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DismissedWelcomeEvents WHERE NostrEventId = @NostrEventId";
        command.Parameters.AddWithValue("@NostrEventId", nostrEventId);

        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static PendingInvite ReadPendingInvite(SqliteDataReader reader)
    {
        return new PendingInvite
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            SenderPublicKey = reader.GetString(reader.GetOrdinal("SenderPublicKey")),
            GroupId = reader.IsDBNull(reader.GetOrdinal("GroupId")) ? null : reader.GetString(reader.GetOrdinal("GroupId")),
            WelcomeData = (byte[])reader["WelcomeData"],
            KeyPackageEventId = reader.IsDBNull(reader.GetOrdinal("KeyPackageEventId")) ? null : reader.GetString(reader.GetOrdinal("KeyPackageEventId")),
            NostrEventId = reader.GetString(reader.GetOrdinal("NostrEventId")),
            ReceivedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ReceivedAt"))),
            SenderDisplayName = reader.IsDBNull(reader.GetOrdinal("SenderDisplayName")) ? null : reader.GetString(reader.GetOrdinal("SenderDisplayName"))
        };
    }

    private static User ReadUser(SqliteDataReader reader)
    {
        return new User
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            PublicKeyHex = reader.GetString(reader.GetOrdinal("PublicKeyHex")),
            Npub = reader.IsDBNull(reader.GetOrdinal("Npub")) ? string.Empty : reader.GetString(reader.GetOrdinal("Npub")),
            PrivateKeyHex = reader.IsDBNull(reader.GetOrdinal("PrivateKeyHex")) ? null : reader.GetString(reader.GetOrdinal("PrivateKeyHex")),
            Nsec = reader.IsDBNull(reader.GetOrdinal("Nsec")) ? null : reader.GetString(reader.GetOrdinal("Nsec")),
            DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
            Username = reader.IsDBNull(reader.GetOrdinal("Username")) ? null : reader.GetString(reader.GetOrdinal("Username")),
            AvatarUrl = reader.IsDBNull(reader.GetOrdinal("AvatarUrl")) ? null : reader.GetString(reader.GetOrdinal("AvatarUrl")),
            About = reader.IsDBNull(reader.GetOrdinal("About")) ? null : reader.GetString(reader.GetOrdinal("About")),
            Nip05 = reader.IsDBNull(reader.GetOrdinal("Nip05")) ? null : reader.GetString(reader.GetOrdinal("Nip05")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastUpdatedAt = reader.IsDBNull(reader.GetOrdinal("LastUpdatedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastUpdatedAt"))),
            IsCurrentUser = reader.GetInt32(reader.GetOrdinal("IsCurrentUser")) == 1
        };
    }

    private static Chat ReadChat(SqliteDataReader reader)
    {
        var welcomeOrdinal = reader.GetOrdinal("WelcomeNostrEventId");
        return new Chat
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Type = (ChatType)reader.GetInt32(reader.GetOrdinal("Type")),
            MlsGroupId = reader.IsDBNull(reader.GetOrdinal("MlsGroupId")) ? null : (byte[])reader["MlsGroupId"],
            MlsEpoch = (ulong)reader.GetInt64(reader.GetOrdinal("MlsEpoch")),
            AvatarUrl = reader.IsDBNull(reader.GetOrdinal("AvatarUrl")) ? null : reader.GetString(reader.GetOrdinal("AvatarUrl")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            UnreadCount = reader.GetInt32(reader.GetOrdinal("UnreadCount")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastActivityAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastActivityAt"))),
            IsMuted = reader.GetInt32(reader.GetOrdinal("IsMuted")) == 1,
            IsPinned = reader.GetInt32(reader.GetOrdinal("IsPinned")) == 1,
            IsArchived = reader.GetInt32(reader.GetOrdinal("IsArchived")) == 1,
            WelcomeNostrEventId = reader.IsDBNull(welcomeOrdinal) ? null : reader.GetString(welcomeOrdinal)
        };
    }

    private static Message ReadMessage(SqliteDataReader reader)
    {
        return new Message
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            ChatId = reader.GetString(reader.GetOrdinal("ChatId")),
            SenderPublicKey = reader.GetString(reader.GetOrdinal("SenderPublicKey")),
            Type = (MessageType)reader.GetInt32(reader.GetOrdinal("Type")),
            Content = reader.GetString(reader.GetOrdinal("Content")),
            EncryptedContent = reader.IsDBNull(reader.GetOrdinal("EncryptedContent")) ? null : reader.GetString(reader.GetOrdinal("EncryptedContent")),
            NostrEventId = reader.IsDBNull(reader.GetOrdinal("NostrEventId")) ? null : reader.GetString(reader.GetOrdinal("NostrEventId")),
            MlsEpoch = (ulong)reader.GetInt64(reader.GetOrdinal("MlsEpoch")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp"))),
            ReceivedAt = reader.IsDBNull(reader.GetOrdinal("ReceivedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ReceivedAt"))),
            Status = (MessageStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            ReplyToMessageId = reader.IsDBNull(reader.GetOrdinal("ReplyToMessageId")) ? null : reader.GetString(reader.GetOrdinal("ReplyToMessageId")),
            IsFromCurrentUser = reader.GetInt32(reader.GetOrdinal("IsFromCurrentUser")) == 1,
            IsEdited = reader.GetInt32(reader.GetOrdinal("IsEdited")) == 1,
            IsDeleted = reader.GetInt32(reader.GetOrdinal("IsDeleted")) == 1
        };
    }

    private static KeyPackage ReadKeyPackage(SqliteDataReader reader)
    {
        return new KeyPackage
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            OwnerPublicKey = reader.GetString(reader.GetOrdinal("OwnerPublicKey")),
            Data = (byte[])reader["Data"],
            NostrEventId = reader.IsDBNull(reader.GetOrdinal("NostrEventId")) ? null : reader.GetString(reader.GetOrdinal("NostrEventId")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ExpiresAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ExpiresAt"))),
            IsUsed = reader.GetInt32(reader.GetOrdinal("IsUsed")) == 1,
            CiphersuiteId = (ushort)reader.GetInt32(reader.GetOrdinal("CiphersuiteId"))
        };
    }

    private static async Task<List<string>> GetChatParticipantsAsync(SqliteConnection connection, string chatId)
    {
        var participants = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT PublicKeyHex FROM ChatParticipants WHERE ChatId = @ChatId";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            participants.Add(reader.GetString(0));
        }

        return participants;
    }

    private static async Task<List<string>> GetChatRelaysAsync(SqliteConnection connection, string chatId)
    {
        var relays = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl FROM ChatRelays WHERE ChatId = @ChatId";
        command.Parameters.AddWithValue("@ChatId", chatId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(reader.GetString(0));
        }

        return relays;
    }

    private static async Task<Dictionary<string, List<string>>> GetMessageReactionsAsync(SqliteConnection connection, string messageId)
    {
        var reactions = new Dictionary<string, List<string>>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Emoji, ReactorPublicKey FROM MessageReactions WHERE MessageId = @MessageId";
        command.Parameters.AddWithValue("@MessageId", messageId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var emoji = reader.GetString(0);
            var reactor = reader.GetString(1);

            if (!reactions.ContainsKey(emoji))
            {
                reactions[emoji] = new List<string>();
            }
            reactions[emoji].Add(reactor);
        }

        return reactions;
    }

    private static async Task<List<string>> GetKeyPackageRelaysAsync(SqliteConnection connection, string keyPackageId)
    {
        var relays = new List<string>();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelayUrl FROM KeyPackageRelays WHERE KeyPackageId = @KeyPackageId";
        command.Parameters.AddWithValue("@KeyPackageId", keyPackageId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relays.Add(reader.GetString(0));
        }

        return relays;
    }
}
