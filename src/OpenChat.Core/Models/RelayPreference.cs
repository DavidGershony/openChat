namespace OpenChat.Core.Models;

/// <summary>
/// How a relay is used: read-only, write-only, or both.
/// Maps to NIP-65 relay list metadata markers.
/// </summary>
public enum RelayUsage
{
    /// <summary>Both read and write (no marker in NIP-65, or omitted).</summary>
    Both,
    /// <summary>Read-only relay (receives events).</summary>
    Read,
    /// <summary>Write-only relay (publishes events).</summary>
    Write
}

/// <summary>
/// A user's relay preference from NIP-65 (kind 10002) events.
/// </summary>
public class RelayPreference
{
    public string Url { get; set; } = string.Empty;
    public RelayUsage Usage { get; set; } = RelayUsage.Both;
}
