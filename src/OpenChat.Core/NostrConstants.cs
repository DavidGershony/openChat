namespace OpenChat.Core;

/// <summary>
/// Default and discovery relay URLs used throughout the app.
/// Centralizes relay configuration to avoid hardcoded strings scattered across the codebase.
/// </summary>
public static class NostrConstants
{
    /// <summary>
    /// Default relays used when no user-specific relay list is available.
    /// </summary>
    public static readonly string[] DefaultRelays =
    {
        "wss://relay.angor.io",
        "wss://relay2.angor.io",
        "wss://nos.lol"
    };

    /// <summary>
    /// Discovery relays that index NIP-65 (kind 10002) relay list metadata.
    /// Used to look up relay preferences for any user.
    /// </summary>
    public static readonly string[] DiscoveryRelays =
    {
        "wss://purplepag.es",
        "wss://relay.nostr.band"
    };
}
