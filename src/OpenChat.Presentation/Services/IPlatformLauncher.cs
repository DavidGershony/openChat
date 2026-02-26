namespace OpenChat.Presentation.Services;

/// <summary>
/// Platform-agnostic launcher for URLs and folders.
/// </summary>
public interface IPlatformLauncher
{
    void OpenUrl(string url);
    void OpenFolder(string path);
}
