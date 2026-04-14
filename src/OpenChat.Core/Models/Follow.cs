namespace OpenChat.Core.Models;

public class Follow
{
    public string PublicKeyHex { get; set; } = string.Empty;
    public string? Petname { get; set; }
    public string? RelayHint { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
