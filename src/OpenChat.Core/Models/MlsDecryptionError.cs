namespace OpenChat.Core.Models;

public class MlsDecryptionError
{
    public string ChatId { get; set; } = string.Empty;
    public string ChatName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
