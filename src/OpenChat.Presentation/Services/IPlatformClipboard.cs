namespace OpenChat.Presentation.Services;

/// <summary>
/// Platform-agnostic clipboard service.
/// </summary>
public interface IPlatformClipboard
{
    Task SetTextAsync(string text);
    Task<string?> GetTextAsync();
}
