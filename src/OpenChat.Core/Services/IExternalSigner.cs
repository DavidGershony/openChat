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
