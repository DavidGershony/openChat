namespace Scramble.Presentation.Services;

/// <summary>
/// Platform-agnostic launcher for URLs, folders, and file sharing.
/// </summary>
public interface IPlatformLauncher
{
    /// <summary>
    /// Whether the app is running on a mobile (single-view) shell.
    /// Used to control navigation affordances like the chat back button.
    /// </summary>
    bool IsMobile => false;

    void OpenUrl(string url);
    void OpenFolder(string path);

    /// <summary>
    /// Shares a file using the platform's native share mechanism.
    /// On Android this launches a share intent; on desktop it opens the containing folder.
    /// </summary>
    /// <param name="filePath">Absolute path to the file to share.</param>
    /// <param name="mimeType">MIME type of the file (e.g. "text/plain").</param>
    /// <param name="title">Title shown in the share chooser.</param>
    void ShareFile(string filePath, string mimeType, string title);
}
