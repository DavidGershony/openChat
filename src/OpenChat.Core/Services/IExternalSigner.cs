namespace OpenChat.Core.Services;

/// <summary>
/// Interface for external Nostr signers (NIP-46, NIP-07 style).
/// Supports signers like Amber, nsecBunker, etc.
/// </summary>
public interface IExternalSigner
{
    /// <summary>
    /// Whether the signer is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// The public key of the connected signer.
    /// </summary>
    string? PublicKeyHex { get; }

    /// <summary>
    /// The npub of the connected signer.
    /// </summary>
    string? Npub { get; }

    /// <summary>
    /// Relay URL used for the NIP-46 connection (for session persistence).
    /// </summary>
    string? RelayUrl { get; }

    /// <summary>
    /// Remote signer's public key (for session persistence).
    /// </summary>
    string? RemotePubKey { get; }

    /// <summary>
    /// Shared secret for the NIP-46 session (for session persistence).
    /// </summary>
    string? Secret { get; }

    /// <summary>
    /// Observable for connection status changes.
    /// </summary>
    IObservable<ExternalSignerStatus> Status { get; }

    /// <summary>
    /// Connect to an external signer using a bunker URL (NIP-46).
    /// Format: bunker://&lt;pubkey&gt;?relay=wss://relay.example.com&amp;secret=&lt;secret&gt;
    /// </summary>
    Task<bool> ConnectAsync(string bunkerUrl);

    /// <summary>
    /// Connect to an external signer using connection string.
    /// Can be bunker URL or nostrconnect URL.
    /// </summary>
    Task<bool> ConnectWithStringAsync(string connectionString);

    /// <summary>
    /// Disconnect from the external signer.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Get the public key from the signer.
    /// </summary>
    Task<string> GetPublicKeyAsync();

    /// <summary>
    /// Sign an event using the external signer.
    /// </summary>
    Task<string> SignEventAsync(UnsignedNostrEvent unsignedEvent);

    /// <summary>
    /// Encrypt content using NIP-44 via the external signer.
    /// </summary>
    Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey);

    /// <summary>
    /// Decrypt content using NIP-44 via the external signer.
    /// </summary>
    Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey);

    /// <summary>
    /// Generate a connection URI for the user to scan/click.
    /// Used when initiating connection from the app side.
    /// </summary>
    string GenerateConnectionUri(string relayUrl);

    /// <summary>
    /// Generate a nostrconnect:// URI, connect to the relay, and listen for an
    /// incoming connection from a remote signer. Returns the URI for QR display.
    /// Status observable will fire Connected when the signer connects.
    /// </summary>
    Task<string> GenerateAndListenForConnectionAsync(string relayUrl);

    /// <summary>
    /// Reconnect to the relay after the WebSocket was dropped (e.g. app backgrounded).
    /// Re-establishes the WebSocket and re-subscribes using existing keys/state.
    /// No-op if already connected or if no prior connection was initiated.
    /// </summary>
    Task ReconnectAsync();

    /// <summary>
    /// Restore a previously authorized session using persisted keypair and session details.
    /// Reuses the original ephemeral keypair (Amber remembers this authorization).
    /// Does NOT send a connect request — just connects WebSocket and subscribes.
    /// </summary>
    Task<bool> RestoreSessionAsync(string relayUrl, string remotePubKey, string localPrivateKeyHex, string localPublicKeyHex, string? secret = null);

    /// <summary>
    /// The ephemeral local private key used for NIP-46 communication.
    /// Needed for session persistence.
    /// </summary>
    string? LocalPrivateKeyHex { get; }

    /// <summary>
    /// The ephemeral local public key used for NIP-46 communication.
    /// Needed for session persistence.
    /// </summary>
    string? LocalPublicKeyHex { get; }
}

public class ExternalSignerStatus
{
    public bool IsConnected { get; set; }
    public string? PublicKeyHex { get; set; }
    public string? Error { get; set; }
    public ExternalSignerState State { get; set; }
}

public enum ExternalSignerState
{
    Disconnected,
    Connecting,
    WaitingForApproval,
    Connected,
    Error
}

/// <summary>
/// Represents an unsigned Nostr event to be signed by external signer.
/// </summary>
public class UnsignedNostrEvent
{
    public int Kind { get; set; }
    public string Content { get; set; } = string.Empty;
    public List<List<string>> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
