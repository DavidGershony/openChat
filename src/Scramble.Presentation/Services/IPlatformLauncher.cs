namespace Scramble.Presentation.Services;

/// <summary>
/// Platform-agnostic launcher for URLs, folders, and file sharing.
/// </summary>
public interface IPlatformLauncher
{
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

    /// <summary>
    /// Launches a nostrconnect:// URI to open an external signer app (e.g. Amber on Android).
    /// On platforms where no signer app is available, this is a no-op.
    /// </summary>
    /// <param name="uri">The nostrconnect:// URI to launch.</param>
    /// <returns>True if the URI was launched successfully, false otherwise.</returns>
    bool LaunchSignerUri(string uri);
}
