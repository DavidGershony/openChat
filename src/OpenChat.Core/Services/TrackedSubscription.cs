namespace OpenChat.Core.Services;

/// <summary>
/// Tracks a Nostr subscription: its filter (source of truth) and which relays carry it.
/// </summary>
public class TrackedSubscription
{
    public string SubId { get; }
    public ParsedFilter Filter { get; }
    public HashSet<string> ActiveRelays { get; } = new();
    public DateTimeOffset CreatedAt { get; }

    public TrackedSubscription(string subId, ParsedFilter filter)
    {
        SubId = subId;
        Filter = filter;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void AddRelay(string relayUrl) => ActiveRelays.Add(relayUrl);

    public void RemoveRelay(string relayUrl) => ActiveRelays.Remove(relayUrl);

    public bool HasRelay(string relayUrl) => ActiveRelays.Contains(relayUrl);
}
