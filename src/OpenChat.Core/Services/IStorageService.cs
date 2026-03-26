using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Service for local SQLite storage operations.
/// </summary>
public interface IStorageService
{
    Task InitializeAsync();

    // User operations
    Task<User?> GetCurrentUserAsync();
    Task SaveCurrentUserAsync(User user);
    Task ClearCurrentUserAsync();
    Task<User?> GetUserByPublicKeyAsync(string publicKeyHex);
    Task SaveUserAsync(User user);
    Task<IEnumerable<User>> GetAllUsersAsync();

    // Chat operations
    Task<Chat?> GetChatAsync(string chatId);
    Task<IEnumerable<Chat>> GetAllChatsAsync();
    Task SaveChatAsync(Chat chat);
    Task DeleteChatAsync(string chatId);

    // Message operations
    Task<Message?> GetMessageAsync(string messageId);
    Task<Dictionary<string, Message>> GetLastMessagePerChatAsync();
    Task<IEnumerable<Message>> GetMessagesForChatAsync(string chatId, int limit = 50, int offset = 0);
    Task SaveMessageAsync(Message message);
    Task UpdateMessageStatusAsync(string messageId, MessageStatus status);
    Task DeleteMessageAsync(string messageId);
    Task<bool> MessageExistsByNostrEventIdAsync(string nostrEventId);

    // KeyPackage operations
    Task<KeyPackage?> GetKeyPackageAsync(string id);
    Task<KeyPackage?> GetKeyPackageByNostrEventIdAsync(string nostrEventId);
    Task<IEnumerable<KeyPackage>> GetUnusedKeyPackagesAsync(string ownerPublicKey);
    Task<IEnumerable<KeyPackage>> GetAllKeyPackagesAsync(string ownerPublicKey);
    Task SaveKeyPackageAsync(KeyPackage keyPackage);
    Task MarkKeyPackageUsedAsync(string id);
    Task UpdateKeyPackageStatusAsync(string id, KeyPackageStatus status);

    // Pending invite operations
    Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync();
    Task SavePendingInviteAsync(PendingInvite invite);
    Task DeletePendingInviteAsync(string inviteId);
    Task DismissWelcomeEventAsync(string nostrEventId);
    Task<bool> IsWelcomeEventDismissedAsync(string nostrEventId);

    // User relay list (NIP-65)
    Task SaveUserRelayListAsync(string publicKeyHex, IEnumerable<RelayPreference> relays);
    Task<List<RelayPreference>> GetUserRelayListAsync(string publicKeyHex);

    // MLS state
    Task SaveMlsStateAsync(string groupId, byte[] state);
    Task<byte[]?> GetMlsStateAsync(string groupId);
    Task DeleteMlsStateAsync(string groupId);

    // Welcome event management
    Task UndismissWelcomeEventAsync(string nostrEventId);

    // App settings (key-value store)
    Task<string?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value);
}
