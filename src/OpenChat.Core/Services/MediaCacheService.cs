using Microsoft.Extensions.Logging;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Services;

/// <summary>
/// Caches decrypted MIP-04 media files to disk so they persist across chat reloads.
/// Files are stored in {ProfileDataDirectory}/media/{messageId}.{ext}
/// </summary>
public class MediaCacheService
{
    private readonly ILogger<MediaCacheService> _logger = LoggingConfiguration.CreateLogger<MediaCacheService>();
    private readonly string _cacheDir;

    public MediaCacheService()
    {
        _cacheDir = Path.Combine(ProfileConfiguration.DataDirectory, "media");
    }

    /// <summary>
    /// Returns the cached decrypted bytes for a message, or null if not cached.
    /// </summary>
    public byte[]? GetCached(string messageId, string? mimeType = null)
    {
        var path = GetCachePath(messageId, mimeType);
        if (!File.Exists(path)) return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            _logger.LogDebug("MediaCache hit: {MessageId} ({Size} bytes)", messageId[..Math.Min(8, messageId.Length)], bytes.Length);
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaCache read failed for {MessageId}", messageId[..Math.Min(8, messageId.Length)]);
            return null;
        }
    }

    /// <summary>
    /// Saves decrypted media bytes to the cache.
    /// </summary>
    public void Save(string messageId, byte[] decryptedBytes, string? mimeType = null)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = GetCachePath(messageId, mimeType);
            File.WriteAllBytes(path, decryptedBytes);
            _logger.LogDebug("MediaCache saved: {MessageId} ({Size} bytes)", messageId[..Math.Min(8, messageId.Length)], decryptedBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediaCache save failed for {MessageId}", messageId[..Math.Min(8, messageId.Length)]);
        }
    }

    /// <summary>
    /// Checks whether cached media exists for a message.
    /// </summary>
    public bool HasCached(string messageId, string? mimeType = null)
        => File.Exists(GetCachePath(messageId, mimeType));

    private string GetCachePath(string messageId, string? mimeType)
    {
        var ext = mimeType switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/mpeg" => ".mp3",
            _ => ".bin"
        };
        return Path.Combine(_cacheDir, $"{messageId}{ext}");
    }
}
