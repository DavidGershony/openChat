namespace OpenChat.Core.Models;

/// <summary>
/// Represents a chat conversation (group or DM).
/// </summary>
public class Chat
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Chat display name (group name or contact name for DMs).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a group chat or direct message.
    /// </summary>
    public ChatType Type { get; set; } = ChatType.DirectMessage;

    /// <summary>
    /// MLS group ID for encrypted group chats.
    /// </summary>
    public byte[]? MlsGroupId { get; set; }

    /// <summary>
    /// Current MLS epoch number for the group.
    /// </summary>
    public ulong MlsEpoch { get; set; }

    /// <summary>
    /// List of participant public keys (hex format).
    /// </summary>
    public List<string> ParticipantPublicKeys { get; set; } = new();

    /// <summary>
    /// Avatar/group picture URL.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Group description (for group chats).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The last message in this chat (for display in chat list).
    /// </summary>
    public Message? LastMessage { get; set; }

    /// <summary>
    /// Number of unread messages.
    /// </summary>
    public int UnreadCount { get; set; }

    /// <summary>
    /// When the chat was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last activity time (for sorting).
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the chat is muted.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Whether the chat is pinned to the top.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Whether the chat is archived.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Nostr relay URLs associated with this chat.
    /// </summary>
    public List<string> RelayUrls { get; set; } = new();
}

public enum ChatType
{
    DirectMessage,
    Group
}
