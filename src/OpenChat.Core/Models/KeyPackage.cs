namespace OpenChat.Core.Models;

/// <summary>
/// Represents an MLS KeyPackage for group membership.
/// </summary>
public class KeyPackage
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Nostr public key of the owner (hex format).
    /// </summary>
    public string OwnerPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Serialized MLS KeyPackage bytes (decoded from base64 content).
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The full JSON-serialized Nostr event (kind 443) containing this KeyPackage.
    /// Required for MLS processing.
    /// </summary>
    public string? EventJson { get; set; }

    /// <summary>
    /// Nostr event ID where this KeyPackage was published (kind 443).
    /// </summary>
    public string? NostrEventId { get; set; }

    /// <summary>
    /// When the KeyPackage was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the KeyPackage expires (should be rotated before this).
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this KeyPackage has been used (can only be used once).
    /// </summary>
    public bool IsUsed { get; set; }

    /// <summary>
    /// MLS ciphersuite identifier.
    /// </summary>
    public ushort CiphersuiteId { get; set; }

    /// <summary>
    /// Relay URLs where this KeyPackage was published.
    /// </summary>
    public List<string> RelayUrls { get; set; } = new();

    /// <summary>
    /// Nostr tags provided by MDK for MIP-00 compliance.
    /// These should be used when publishing the KeyPackage event.
    /// </summary>
    public List<List<string>> NostrTags { get; set; } = new();

    /// <summary>
    /// Creates a new KeyPackage with default expiration (30 days).
    /// </summary>
    public static KeyPackage Create(string ownerPublicKey, byte[] data, ushort ciphersuiteId = 0x0001)
    {
        return new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            OwnerPublicKey = ownerPublicKey,
            Data = data,
            CiphersuiteId = ciphersuiteId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
    }
}
