namespace OpenChat.Core.Models;

public class Contact
{
    public string PublicKeyHex { get; set; } = string.Empty;
    public string? Petname { get; set; }
    public string Source { get; set; } = "group";
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastInteractedAt { get; set; } = DateTime.UtcNow;
}
