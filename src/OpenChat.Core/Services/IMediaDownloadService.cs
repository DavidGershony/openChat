namespace OpenChat.Core.Services;

/// <summary>
/// Metadata about a remote media file from a HEAD request.
/// </summary>
public class MediaFileInfo
{
    /// <summary>Content-Length from the server, null if not reported.</summary>
    public long? Size { get; init; }

    /// <summary>Content-Type from the server.</summary>
    public string? ContentType { get; init; }

    /// <summary>Human-readable size string (e.g. "2.4 MB").</summary>
    public string SizeDisplay => Size switch
    {
        null => "unknown size",
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F1} MB"
    };
}

/// <summary>
/// Service for downloading media files with security validations.
/// </summary>
public interface IMediaDownloadService
{
    /// <summary>
    /// Gets file metadata via a HEAD request.
    /// </summary>
    Task<MediaFileInfo> GetFileInfoAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Downloads a file with size enforcement.
    /// </summary>
    Task<byte[]> DownloadAsync(string url, long maxSizeBytes, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the URL points to a known Blossom server.
    /// </summary>
    bool IsKnownBlossomServer(string url);

    /// <summary>
    /// Validates a URL for safe downloading (HTTPS, no private IPs, etc.).
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    Task<string?> ValidateUrlAsync(string url, CancellationToken ct = default);
}
