using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Service for Nostr relay communication.
/// </summary>
public interface INostrService
{
    /// <summary>
    /// Observable stream of connection status changes.
    /// </summary>
    IObservable<NostrConnectionStatus> ConnectionStatus { get; }

    /// <summary>
    /// Observable stream of received Nostr events.
    /// </summary>
    IObservable<NostrEventReceived> Events { get; }

    /// <summary>
    /// Connect to a Nostr relay.
    /// </summary>
    Task ConnectAsync(string relayUrl);

    /// <summary>
    /// Connect to multiple relays.
    /// </summary>
    Task ConnectAsync(IEnumerable<string> relayUrls);

    /// <summary>
    /// Disconnect from all relays.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Reconnect a single relay by URL.
    /// </summary>
    Task ReconnectRelayAsync(string relayUrl);

    /// <summary>
    /// Disconnect and remove a single relay.
    /// </summary>
    Task DisconnectRelayAsync(string relayUrl);

    /// <summary>
    /// Generate a new Nostr keypair.
    /// </summary>
    (string privateKeyHex, string publicKeyHex, string nsec, string npub) GenerateKeyPair();

    /// <summary>
    /// Import an existing private key (nsec or hex).
    /// </summary>
    (string privateKeyHex, string publicKeyHex, string nsec, string npub) ImportPrivateKey(string privateKey);

    /// <summary>
    /// Subscribe to events by filter.
    /// </summary>
    Task SubscribeAsync(string subscriptionId, NostrFilter filter);

    /// <summary>
    /// Unsubscribe from a subscription.
    /// </summary>
    Task UnsubscribeAsync(string subscriptionId);

    /// <summary>
    /// Publish a KeyPackage (kind 443).
    /// </summary>
    /// <param name="keyPackageData">Base64-encoded KeyPackage content bytes.</param>
    /// <param name="privateKeyHex">Sender's private key.</param>
    /// <param name="mdkTags">Optional MDK-provided tags for MIP-00 compliance.</param>
    Task<string> PublishKeyPackageAsync(byte[] keyPackageData, string privateKeyHex, List<List<string>>? mdkTags = null);

    /// <summary>
    /// Publish a Welcome message (kind 444).
    /// </summary>
    /// <param name="welcomeData">MLS Welcome message data.</param>
    /// <param name="recipientPublicKey">Recipient's public key.</param>
    /// <param name="privateKeyHex">Sender's private key.</param>
    /// <param name="keyPackageEventId">Optional KeyPackage event ID (required per MIP-02).</param>
    Task<string> PublishWelcomeAsync(byte[] welcomeData, string recipientPublicKey, string privateKeyHex, string? keyPackageEventId = null);

    /// <summary>
    /// Publish a commit/evolution message (kind 445).
    /// Should be published before sending Welcome messages.
    /// </summary>
    Task<string> PublishCommitAsync(byte[] commitData, string groupId, string privateKeyHex);

    /// <summary>
    /// Publish a group message (kind 445).
    /// </summary>
    Task<string> PublishGroupMessageAsync(byte[] encryptedData, string groupId, string privateKeyHex);

    /// <summary>
    /// Fetch KeyPackages for a user.
    /// </summary>
    Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex);

    /// <summary>
    /// Fetch user metadata (kind 0) for a public key.
    /// </summary>
    Task<UserMetadata?> FetchUserMetadataAsync(string publicKeyHex);

    /// <summary>
    /// Subscribe to Welcome messages (kind 444) for a user.
    /// Returns an observable of welcome events.
    /// </summary>
    Task SubscribeToWelcomesAsync(string publicKeyHex);

    /// <summary>
    /// Directly fetch Welcome events (kind 444) for a user from relays.
    /// Opens fresh connections, queries, and returns results.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchWelcomeEventsAsync(string publicKeyHex);

    /// <summary>
    /// Subscribe to group messages (kind 445) for specific groups.
    /// </summary>
    Task SubscribeToGroupMessagesAsync(IEnumerable<string> groupIds);

    /// <summary>
    /// Observable stream of Welcome messages (kind 444).
    /// </summary>
    IObservable<MarmotWelcomeEvent> WelcomeMessages { get; }

    /// <summary>
    /// Observable stream of Group messages (kind 445).
    /// </summary>
    IObservable<MarmotGroupMessageEvent> GroupMessages { get; }

    /// <summary>
    /// Convert npub to hex public key.
    /// </summary>
    string? NpubToHex(string npub);

    /// <summary>
    /// Encrypt content using NIP-44.
    /// </summary>
    byte[] EncryptNip44(string plaintext, string senderPrivateKeyHex, string recipientPublicKeyHex);

    /// <summary>
    /// Decrypt content using NIP-44.
    /// </summary>
    string DecryptNip44(byte[] ciphertext, string recipientPrivateKeyHex, string senderPublicKeyHex);
}

public class NostrConnectionStatus
{
    public string RelayUrl { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public string? Error { get; set; }
    public bool Removed { get; set; }
}

public class NostrEventReceived
{
    public int Kind { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<List<string>> Tags { get; set; } = new();
    public string RelayUrl { get; set; } = string.Empty;
}

public class NostrFilter
{
    public List<int>? Kinds { get; set; }
    public List<string>? Authors { get; set; }
    public List<string>? Ids { get; set; }
    public List<string>? TagP { get; set; }
    public List<string>? TagE { get; set; }
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
    public int? Limit { get; set; }
}

/// <summary>
/// User metadata from Nostr kind 0 events.
/// </summary>
public class UserMetadata
{
    public string PublicKeyHex { get; set; } = string.Empty;
    public string? Npub { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public string? About { get; set; }
    public string? Picture { get; set; }
    public string? Banner { get; set; }
    public string? Website { get; set; }
    public string? Nip05 { get; set; }
    public string? Lud16 { get; set; }
    public string? Lud06 { get; set; }
    public DateTime? CreatedAt { get; set; }

    public string GetDisplayName()
    {
        if (!string.IsNullOrEmpty(DisplayName))
            return DisplayName;
        if (!string.IsNullOrEmpty(Name))
            return Name;
        if (!string.IsNullOrEmpty(Username))
            return $"@{Username}";
        if (!string.IsNullOrEmpty(Npub))
            return $"{Npub[..12]}...";
        if (!string.IsNullOrEmpty(PublicKeyHex))
            return $"{PublicKeyHex[..12]}...";
        return "Unknown";
    }
}

/// <summary>
/// Marmot Welcome message event (kind 444).
/// Used to invite users to MLS groups.
/// </summary>
public class MarmotWelcomeEvent
{
    /// <summary>
    /// Nostr event ID.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the sender (inviter).
    /// </summary>
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the recipient (invitee).
    /// </summary>
    public string RecipientPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded MLS Welcome message.
    /// </summary>
    public string WelcomeData { get; set; } = string.Empty;

    /// <summary>
    /// Group ID if specified in tags (h-tag).
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// KeyPackage event ID referenced in the e-tag (MIP-02 requirement).
    /// </summary>
    public string? KeyPackageEventId { get; set; }

    /// <summary>
    /// Relay URLs from the relays tag for locating Group Events.
    /// </summary>
    public List<string> RelayHints { get; set; } = new();

    /// <summary>
    /// When the event was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Relay URL where this event was received.
    /// </summary>
    public string RelayUrl { get; set; } = string.Empty;
}

/// <summary>
/// Marmot Group message event (kind 445).
/// Contains MLS-encrypted group messages or commits.
/// </summary>
public class MarmotGroupMessageEvent
{
    /// <summary>
    /// Nostr event ID.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Public key of the sender.
    /// </summary>
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Group ID from tags.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded MLS ciphertext.
    /// </summary>
    public string EncryptedData { get; set; } = string.Empty;

    /// <summary>
    /// MLS epoch if specified in tags.
    /// </summary>
    public ulong? Epoch { get; set; }

    /// <summary>
    /// When the event was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Relay URL where this event was received.
    /// </summary>
    public string RelayUrl { get; set; } = string.Empty;
}
