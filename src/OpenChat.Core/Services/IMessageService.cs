using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Service for message handling and chat operations.
/// </summary>
public interface IMessageService
{
    /// <summary>
    /// Observable stream of new messages.
    /// </summary>
    IObservable<Message> NewMessages { get; }

    /// <summary>
    /// Observable stream of message status updates.
    /// </summary>
    IObservable<(string MessageId, MessageStatus Status)> MessageStatusUpdates { get; }

    /// <summary>
    /// Observable stream of chat updates.
    /// </summary>
    IObservable<Chat> ChatUpdates { get; }

    /// <summary>
    /// Observable stream of new pending invites.
    /// </summary>
    IObservable<PendingInvite> NewInvites { get; }

    /// <summary>
    /// Observable stream of reaction updates (messageId, emoji, reactorPublicKey).
    /// </summary>
    IObservable<(string MessageId, string Emoji, string ReactorPublicKey)> ReactionUpdates { get; }

    /// <summary>
    /// Observable stream of MLS decryption errors (e.g. corrupted state after restart).
    /// </summary>
    IObservable<MlsDecryptionError> DecryptionErrors { get; }

    /// <summary>
    /// Initialize the message service.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Get all chats for the current user.
    /// </summary>
    Task<IEnumerable<Chat>> GetChatsAsync();

    /// <summary>
    /// Get a specific chat.
    /// </summary>
    Task<Chat?> GetChatAsync(string chatId);

    /// <summary>
    /// Get messages for a chat.
    /// </summary>
    Task<IEnumerable<Message>> GetMessagesAsync(string chatId, int limit = 50, int offset = 0);

    /// <summary>
    /// Send a text message.
    /// </summary>
    Task<Message> SendMessageAsync(string chatId, string content);

    /// <summary>
    /// Send a voice message with encrypted audio uploaded to Blossom.
    /// </summary>
    Task SendVoiceMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, double durationSeconds);

    /// <summary>
    /// Send a media message (image, file) with encrypted data uploaded to Blossom.
    /// </summary>
    Task SendMediaMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, MessageType messageType);

    /// <summary>
    /// Send a reply to a message.
    /// </summary>
    Task<Message> SendReplyAsync(string chatId, string content, string replyToMessageId);

    /// <summary>
    /// Create a new group chat.
    /// </summary>
    Task<Chat> CreateGroupAsync(string name, IEnumerable<string> memberPublicKeys);

    /// <summary>
    /// Create or get a direct message chat with a user.
    /// </summary>
    Task<Chat> GetOrCreateDirectMessageAsync(string recipientPublicKey);

    /// <summary>
    /// Create or get a bot chat (NIP-17 DMs) with a bot by public key.
    /// </summary>
    Task<Chat> GetOrCreateBotChatAsync(string botPublicKey);

    /// <summary>
    /// Add a member to a group.
    /// </summary>
    Task AddMemberAsync(string chatId, string memberPublicKey);

    /// <summary>
    /// Remove a member from a group.
    /// </summary>
    Task RemoveMemberAsync(string chatId, string memberPublicKey);

    /// <summary>
    /// Leave a group chat.
    /// </summary>
    Task LeaveGroupAsync(string chatId);

    /// <summary>
    /// Mark messages as read.
    /// </summary>
    Task MarkAsReadAsync(string chatId);

    /// <summary>
    /// Add a reaction to a message.
    /// </summary>
    Task AddReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Send a reaction to the MLS group and update local storage.
    /// </summary>
    Task SendReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Remove a reaction from a message.
    /// </summary>
    Task RemoveReactionAsync(string messageId, string emoji);

    /// <summary>
    /// Delete a message.
    /// </summary>
    Task DeleteMessageAsync(string messageId);

    /// <summary>
    /// Archive a chat.
    /// </summary>
    Task ArchiveChatAsync(string chatId);

    /// <summary>
    /// Mute/unmute a chat.
    /// </summary>
    Task SetMutedAsync(string chatId, bool muted);

    /// <summary>
    /// Get all pending group invites.
    /// </summary>
    Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync();

    /// <summary>
    /// Accept a pending invite — processes MLS welcome and creates the chat.
    /// </summary>
    Task<Chat> AcceptInviteAsync(string inviteId);

    /// <summary>
    /// Decline a pending invite — discards it.
    /// </summary>
    Task DeclineInviteAsync(string inviteId);

    /// <summary>
    /// Rescan relays for Welcome messages (kind 444) and reload pending invites.
    /// </summary>
    Task RescanInvitesAsync();

    /// <summary>
    /// Reset a group's MLS state and delete the chat, allowing re-join via welcome rescan.
    /// </summary>
    Task ResetGroupAsync(string chatId);

    /// <summary>
    /// Audit KeyPackages: fetch own KeyPackages from relays, check which ones
    /// have local private key material, and mark lost ones.
    /// Returns (total on relays, active with keys, lost without keys).
    /// </summary>
    Task<KeyPackageAuditResult> AuditKeyPackagesAsync();

    /// <summary>
    /// Load older messages from relays for the given chat, going back from the fetchBoundary.
    /// Uses a 2-day sliding window with retry. Returns decrypted messages and the new boundary.
    /// </summary>
    Task<LoadOlderMessagesResult> LoadOlderMessagesAsync(string chatId, DateTimeOffset fetchBoundary);

    /// <summary>
    /// Fetches kind 0 metadata for the given public key, caches it as a User in the database,
    /// and returns the metadata. Returns null if fetch fails. Existing user fields (private keys,
    /// signer details) are preserved on update.
    /// </summary>
    Task<UserMetadata?> FetchAndCacheProfileAsync(string publicKeyHex);
}

public class KeyPackageAuditResult
{
    public int TotalOnRelays { get; set; }
    public int ActiveWithKeys { get; set; }
    public int Lost { get; set; }
    public int Expired { get; set; }
    public List<string> LostEventIds { get; set; } = new();
}

public class LoadOlderMessagesResult
{
    public List<Message> Messages { get; set; } = new();
    public DateTimeOffset NewBoundary { get; set; }
    public bool HasMore { get; set; } = true;
}
