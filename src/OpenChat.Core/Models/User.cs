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
    /// Local file path of the cached avatar image.
    /// </summary>
    public string? LocalAvatarPath { get; set; }

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

    /// <summary>
    /// Whether this user authenticates via an external signer (e.g. Amber / NIP-46)
    /// rather than holding a local private key.
    /// </summary>
    public bool IsRemoteSigner => string.IsNullOrEmpty(PrivateKeyHex) && !string.IsNullOrEmpty(SignerRemotePubKey);

    /// <summary>
    /// Relay URL for NIP-46 external signer session (persisted for auto-reconnect on restart).
    /// </summary>
    public string? SignerRelayUrl { get; set; }

    /// <summary>
    /// Remote public key of the NIP-46 signer (persisted for auto-reconnect on restart).
    /// </summary>
    public string? SignerRemotePubKey { get; set; }

    /// <summary>
    /// Shared secret for the NIP-46 signer session (persisted for auto-reconnect on restart).
    /// </summary>
    public string? SignerSecret { get; set; }

    /// <summary>
    /// Ephemeral local private key for NIP-46 communication (persisted for auto-reconnect).
    /// Must reuse the same keypair — Amber authorized this specific pubkey.
    /// </summary>
    public string? SignerLocalPrivateKeyHex { get; set; }

    /// <summary>
    /// Ephemeral local public key for NIP-46 communication (persisted for auto-reconnect).
    /// </summary>
    public string? SignerLocalPublicKeyHex { get; set; }

    public string GetDisplayNameOrNpub()
    {
        if (!string.IsNullOrEmpty(DisplayName))
            return DisplayName;
        if (!string.IsNullOrEmpty(Username))
            return $"@{Username}";
        return "Anonymous";
    }
}
