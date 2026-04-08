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

    /// <summary>
    /// Validates a URL for safe launching. Returns the sanitized absolute URI if valid, null if blocked.
    /// </summary>
    internal static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedUrlSchemes.Contains(uri.Scheme))
        {
            _logger.LogWarning("Blocked OpenUrl with disallowed scheme or invalid URL: {Scheme}",
                Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Scheme : "invalid");
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// Validates a folder path for safe launching. Returns the resolved path if valid, null if blocked.
    /// </summary>
    internal static string? ValidateFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        var dataDir = Path.GetFullPath(ProfileConfiguration.DataDirectory);
        var rootDir = Path.GetFullPath(ProfileConfiguration.RootDataDirectory);

        if (!fullPath.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Blocked OpenFolder outside app directories: {Path}", fullPath);
            return null;
        }

        return fullPath;
    }

    public void OpenUrl(string url)
    {
        var validatedUrl = ValidateUrl(url);
        if (validatedUrl == null)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = validatedUrl,
            UseShellExecute = true
        });
    }

    public void OpenFolder(string path)
    {
        var validatedPath = ValidateFolderPath(path);
        if (validatedPath == null)
            return;

        if (Directory.Exists(validatedPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = validatedPath,
                UseShellExecute = true
            });
        }
    }
}
