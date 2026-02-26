namespace OpenChat.Core.Models;

/// <summary>
/// Represents a Nostr user identity.
/// </summary>
public class User
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Nostr public key in hex format.
    /// </summary>
    public string PublicKeyHex { get; set; } = string.Empty;

    /// <summary>
    /// Nostr public key in npub (NIP-19 bech32) format.
    /// </summary>
    public string Npub { get; set; } = string.Empty;

    /// <summary>
    /// Nostr private key in hex format (only for current user).
    /// </summary>
    public string? PrivateKeyHex { get; set; }

    /// <summary>
    /// Nostr private key in nsec (NIP-19 bech32) format (only for current user).
    /// </summary>
    public string? Nsec { get; set; }

    /// <summary>
    /// Display name from Nostr profile (kind 0 metadata).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Username/handle from Nostr profile.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Profile picture URL.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// About/bio from Nostr profile.
    /// </summary>
    public string? About { get; set; }

    /// <summary>
    /// NIP-05 identifier (e.g., user@domain.com).
    /// </summary>
    public string? Nip05 { get; set; }

    /// <summary>
    /// When the user was first seen/added.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time profile metadata was updated.
    /// </summary>
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>
    /// Whether this is the current logged-in user.
    /// </summary>
    public bool IsCurrentUser { get; set; }

    public string GetDisplayNameOrNpub()
    {
        if (!string.IsNullOrEmpty(DisplayName))
            return DisplayName;
        if (!string.IsNullOrEmpty(Username))
            return $"@{Username}";
        if (!string.IsNullOrEmpty(Npub))
            return $"{Npub[..12]}...";
        return "Unknown";
    }
}
