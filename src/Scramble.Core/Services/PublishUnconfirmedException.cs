namespace Scramble.Core.Services;

/// <summary>
/// Thrown when a Nostr event was sent to relays but no relay confirmed acceptance (OK).
/// Per MIP-03, the caller MUST NOT apply state changes locally when this happens.
/// </summary>
public class PublishUnconfirmedException : InvalidOperationException
{
    /// <summary>The Nostr event ID that failed to be confirmed.</summary>
    public string EventId { get; }

    /// <summary>The Nostr event kind (e.g. 445 for commit/group message, 1059 for welcome).</summary>
    public int Kind { get; }

    /// <summary>The rejection reason from relays, if available.</summary>
    public string? RejectionReason { get; }

    public PublishUnconfirmedException(string eventId, int kind, string? reason)
        : base($"Event {eventId} (kind {kind}) was not confirmed by any relay: {reason ?? "timeout"}")
    {
        EventId = eventId;
        Kind = kind;
        RejectionReason = reason;
    }
}
