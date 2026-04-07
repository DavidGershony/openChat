namespace OpenChat.Core.Models;

public class PendingInvite
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SenderPublicKey { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public byte[] WelcomeData { get; set; } = Array.Empty<byte>();
    public string? KeyPackageEventId { get; set; }
    public string NostrEventId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string? SenderDisplayName { get; set; }
    /// <summary>
    /// Relay URLs from the welcome event's "relays" tag.
    /// Used to set Chat.RelayUrls when the invite is accepted.
    /// </summary>
    public List<string> RelayUrls { get; set; } = new();
}
