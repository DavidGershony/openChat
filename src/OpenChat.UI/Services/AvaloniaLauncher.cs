using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;
using OpenChat.Presentation.Services;

namespace OpenChat.UI.Services;

public class AvaloniaLauncher : IPlatformLauncher
{
    private static readonly ILogger<AvaloniaLauncher> _logger = LoggingConfiguration.CreateLogger<AvaloniaLauncher>();

    /// <summary>
    /// Allowed URL schemes for OpenUrl. Only https is permitted to prevent command injection
    /// via crafted URIs (e.g. cmd.exe, file://, javascript:).
    /// </summary>
    private static readonly HashSet<string> AllowedUrlSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https"
    };

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedUrlSchemes.Contains(uri.Scheme))
        {
            _logger.LogWarning("Blocked OpenUrl with disallowed scheme or invalid URL: {Scheme}",
                Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Scheme : "invalid");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Resolve to full path and verify it's within known app directories
        var fullPath = Path.GetFullPath(path);
        var dataDir = Path.GetFullPath(ProfileConfiguration.DataDirectory);
        var rootDir = Path.GetFullPath(ProfileConfiguration.RootDataDirectory);

        if (!fullPath.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Blocked OpenFolder outside app directories: {Path}", fullPath);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
    }
}
