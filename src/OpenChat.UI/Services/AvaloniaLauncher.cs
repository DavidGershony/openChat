using System.Diagnostics;
using OpenChat.Presentation.Services;

namespace OpenChat.UI.Services;

public class AvaloniaLauncher : IPlatformLauncher
{
    public void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }
}
