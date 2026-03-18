namespace OpenChat.Core.Models;

/// <summary>
/// Represents a chat message.
/// </summary>
public class Message
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// ID of the chat this message belongs to.
    /// </summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>
    /// Nostr public key of the sender (hex format).
    /// </summary>
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The sender user (populated for display).
    /// </summary>
    public User? Sender { get; set; }

    /// <summary>
    /// Message content type.
    /// </summary>
    public MessageType Type { get; set; } = MessageType.Text;

    /// <summary>
    /// Decrypted message content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Raw encrypted content (NIP-44 encrypted).
    /// </summary>
    public string? EncryptedContent { get; set; }

    /// <summary>
    /// Image URL for image messages (from imeta tags, MIP-04).
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Original filename for image/file messages.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// SHA-256 hash of the original file (hex, from imeta tag). Used for MIP-04 decryption.
    /// </summary>
    public string? FileSha256 { get; set; }

    /// <summary>
    /// Encryption nonce (hex, from imeta tag). Used for MIP-04 decryption.
    /// </summary>
    public string? EncryptionNonce { get; set; }

    /// <summary>
    /// MIME type (from imeta tag, e.g. "image/jpeg"). Used for MIP-04 decryption.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>
    /// MIP-04 encryption version from imeta tag.
    /// </summary>
    public string? EncryptionVersion { get; set; }

    /// <summary>
    /// Nostr event ID for this message.
    /// </summary>
    public string? NostrEventId { get; set; }

    /// <summary>
    /// MLS epoch when this message was sent.
    /// </summary>
    public ulong MlsEpoch { get; set; }

    /// <summary>
    /// When the message was created/sent.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the message was received locally.
    /// </summary>
    public DateTime? ReceivedAt { get; set; }

    /// <summary>
    /// Message delivery status.
    /// </summary>
    public MessageStatus Status { get; set; } = MessageStatus.Pending;

    /// <summary>
    /// ID of message being replied to.
    /// </summary>
    public string? ReplyToMessageId { get; set; }

    /// <summary>
    /// The message being replied to (populated for display).
    /// </summary>
    public Message? ReplyToMessage { get; set; }

    /// <summary>
    /// Whether this message is from the current user.
    /// </summary>
    public bool IsFromCurrentUser { get; set; }

    /// <summary>
    /// Whether this message has been edited.
    /// </summary>
    public bool IsEdited { get; set; }

    /// <summary>
    /// Whether this message has been deleted.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Reactions to this message (emoji -> list of reactor public keys).
    /// </summary>
    public Dictionary<string, List<string>> Reactions { get; set; } = new();
}

public enum MessageType
{
    Text,
    Image,
    File,
    System
}

public enum MessageStatus
{
    Pending,
    Sent,
    Delivered,
    Read,
    Failed
}
