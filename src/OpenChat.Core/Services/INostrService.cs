using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Service for Nostr relay communication.
/// </summary>
public interface INostrService
{
    /// <summary>
    /// URLs of currently connected relays.
    /// </summary>
    IReadOnlyList<string> ConnectedRelayUrls { get; }

    /// <summary>
    /// URLs of all configured relays (connected or not).
    /// </summary>
    IReadOnlyList<string> ConfiguredRelayUrls { get; }

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
    /// Connect to relay(s) for bot chat DM delivery/reception only.
    /// These relays receive welcome/gift-wrap subscriptions but are excluded
    /// from group message, key package, and commit broadcasts.
    /// </summary>
    Task ConnectBotRelaysAsync(IEnumerable<string> relayUrls);

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
    /// Set an external signer for event signing (NIP-46).
    /// When set, publish methods can sign events without a private key.
    /// </summary>
    void SetExternalSigner(IExternalSigner? signer);

    /// <summary>
    /// Set the user's private key for NIP-42 relay authentication.
    /// Call this before connecting to relays so AUTH challenges can be answered.
    /// </summary>
    void SetAuthCredentials(string? privateKeyHex);

    /// <summary>
    /// Publish a KeyPackage (kind 30443).
    /// </summary>
    /// <param name="keyPackageData">Base64-encoded KeyPackage content bytes.</param>
    /// <param name="privateKeyHex">Sender's private key (null when using external signer).</param>
    /// <param name="mdkTags">Optional MDK-provided tags for MIP-00 compliance.</param>
    Task<string> PublishKeyPackageAsync(byte[] keyPackageData, string? privateKeyHex, List<List<string>>? mdkTags = null);

    /// <summary>
    /// Publish a Welcome message (kind 444).
    /// </summary>
    /// <param name="welcomeData">MLS Welcome message data.</param>
    /// <param name="recipientPublicKey">Recipient's public key.</param>
    /// <param name="privateKeyHex">Sender's private key (null when using external signer).</param>
    /// <param name="keyPackageEventId">Optional KeyPackage event ID (required per MIP-02).</param>
    Task<string> PublishWelcomeAsync(byte[] welcomeData, string recipientPublicKey, string? privateKeyHex, string? keyPackageEventId = null);

    /// <summary>
    /// Publish a commit/evolution message (kind 445).
    /// Should be published before sending Welcome messages.
    /// </summary>
    Task<string> PublishCommitAsync(byte[] commitData, string groupId, string? privateKeyHex);

    /// <summary>
    /// Publish a group message (kind 445).
    /// </summary>
    Task<string> PublishGroupMessageAsync(byte[] encryptedData, string groupId, string? privateKeyHex);

    /// <summary>
    /// Publish a pre-built signed Nostr event JSON directly to all connected relays.
    /// Returns the event ID extracted from the JSON.
    /// </summary>
    Task<string> PublishRawEventJsonAsync(byte[] eventJsonBytes);

    /// <summary>
    /// Waits for at least one relay to confirm acceptance of a published event.
    /// Returns (accepted, reason) — reason is null on success, contains rejection message or "timeout" on failure.
    /// </summary>
    Task<(bool accepted, string? reason)> WaitForRelayOkAsync(string eventId, int timeoutMs = 5000);

    /// <summary>
    /// Fetch KeyPackages for a user.
    /// </summary>
    Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex);

    /// <summary>
    /// Fetch user metadata (kind 0) for a public key.
    /// </summary>
    Task<UserMetadata?> FetchUserMetadataAsync(string publicKeyHex);

    /// <summary>
    /// Publish user metadata (kind 0) to all connected relays.
    /// </summary>
    Task<string> PublishMetadataAsync(string name, string? displayName, string? about, string? picture, string? privateKeyHex);

    /// <summary>
    /// Subscribe to Welcome messages (NIP-59 gift-wrapped kind 1059) for a user.
    /// The private key is needed to decrypt the gift wrap.
    /// </summary>
    Task SubscribeToWelcomesAsync(string publicKeyHex, string? privateKeyHex = null);

    /// <summary>
    /// Directly fetch Welcome events (kind 444) for a user from relays.
    /// Opens fresh connections, queries, and returns results.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchWelcomeEventsAsync(string publicKeyHex, string? privateKeyHex = null);

    /// <summary>
    /// Subscribe to group messages (kind 445) for specific groups.
    /// </summary>
    Task SubscribeToGroupMessagesAsync(IEnumerable<string> groupIds, DateTimeOffset? since = null);


    /// <summary>
    /// Observable stream of Welcome messages (kind 444).
    /// </summary>
    IObservable<MarmotWelcomeEvent> WelcomeMessages { get; }

    /// <summary>
    /// Observable stream of Group messages (kind 445).
    /// </summary>
    IObservable<MarmotGroupMessageEvent> GroupMessages { get; }

    /// <summary>
    /// Fetch historical group message events (kind 445) for a group from relays.
    /// Uses a time window with since/until and a limit.
    /// </summary>
    Task<IEnumerable<NostrEventReceived>> FetchGroupHistoryAsync(
        string nostrGroupIdHex, DateTimeOffset since, DateTimeOffset until, int limit = 50);

    /// <summary>
    /// Fetch a user's NIP-65 relay list (kind 10002) from discovery relays.
    /// </summary>
    Task<List<RelayPreference>> FetchRelayListAsync(string publicKeyHex);

    /// <summary>
    /// Fetch a user's NIP-02 following list (kind 3) from relays.
    /// Returns the parsed p-tag contacts with optional relay hint and petname.
    /// </summary>
    Task<List<Follow>> FetchFollowingListAsync(string publicKeyHex);

    /// <summary>
    /// Publish the current user's NIP-65 relay list (kind 10002).
    /// </summary>
    Task<string> PublishRelayListAsync(List<RelayPreference> relays, string? privateKeyHex);

    /// <summary>
    /// Convert npub to hex public key.
    /// </summary>
    string? NpubToHex(string npub);

    /// <summary>
    /// Async NIP-44 encryption. Delegates to external signer when connected,
    /// otherwise uses local private key.
    /// </summary>
    Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey);

    /// <summary>
    /// Async NIP-44 decryption. Delegates to external signer when connected,
    /// otherwise uses local private key.
    /// </summary>
    Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey);

    /// <summary>
    /// Publish a NIP-59 gift-wrapped event.
    /// Used for NIP-17 DMs (kind 14 rumor) and other gift-wrapped protocols.
    /// </summary>
    /// <param name="targetRelayUrls">When provided, only send to these relay URLs (must be connected). When null, broadcast to all connected relays.</param>
    Task<string> PublishGiftWrapAsync(
        int rumorKind, string content, List<List<string>> rumorTags,
        string? senderPrivateKeyHex, string senderPublicKeyHex,
        string recipientPublicKeyHex, List<string>? targetRelayUrls = null);
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
    public string Signature { get; set; } = string.Empty;
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
