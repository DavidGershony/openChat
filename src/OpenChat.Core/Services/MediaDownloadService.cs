using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Services;

/// <summary>
/// Secure media download service with URL validation and size enforcement.
/// </summary>
public class MediaDownloadService : IMediaDownloadService
{
    private readonly ILogger<MediaDownloadService> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Known Blossom media hosting servers.
    /// </summary>
    private static readonly HashSet<string> KnownBlossomServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "blossom.band",
        "nostr.build",
        "void.cat",
        "files.sovbit.host",
        "cdn.satellite.earth",
        "nostr.download",
        "media.nostr.band",
        "blossom.primal.net"
    };

    public MediaDownloadService(HttpClient? httpClient = null)
    {
        _logger = LoggingConfiguration.CreateLogger<MediaDownloadService>();

        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // Set a reasonable user agent
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OpenChat/0.1");
        }

        _logger.LogInformation("MediaDownloadService initialized");
    }

    public async Task<MediaFileInfo> GetFileInfoAsync(string url, CancellationToken ct = default)
    {
        _logger.LogInformation("GetFileInfo: HEAD request to {Url}", SanitizeUrlForLog(url));

        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var size = response.Content.Headers.ContentLength;
        var contentType = response.Content.Headers.ContentType?.MediaType;

        var info = new MediaFileInfo
        {
            Size = size,
            ContentType = contentType
        };

        _logger.LogInformation("GetFileInfo: size={Size}, contentType={ContentType}",
            info.SizeDisplay, contentType ?? "(none)");

        return info;
    }

    public async Task<byte[]> DownloadAsync(string url, long maxSizeBytes, CancellationToken ct = default)
    {
        _logger.LogInformation("Download: starting download from {Url}, maxSize={MaxSize} bytes",
            SanitizeUrlForLog(url), maxSizeBytes);

        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Check content-length header first
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxSizeBytes)
        {
            _logger.LogWarning("Download: content-length {Size} exceeds max {Max}", contentLength.Value, maxSizeBytes);
            throw new InvalidOperationException(
                $"File too large: {FormatSize(contentLength.Value)}. Maximum allowed: {FormatSize(maxSizeBytes)}.");
        }

        // Stream download with size enforcement
        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var memoryStream = new MemoryStream();

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > maxSizeBytes)
            {
                _logger.LogWarning("Download: stream exceeded max size {Max} at {Total} bytes", maxSizeBytes, totalRead);
                throw new InvalidOperationException(
                    $"File too large: exceeded {FormatSize(maxSizeBytes)} limit during download.");
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }

        var result = memoryStream.ToArray();
        _logger.LogInformation("Download: completed, {Size} bytes", result.Length);
        return result;
    }

    public bool IsKnownBlossomServer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;

        // Check if the host or any parent domain is in the known list
        if (KnownBlossomServers.Contains(host))
            return true;

        // Check parent domains (e.g., "cdn.blossom.band" -> "blossom.band")
        var parts = host.Split('.');
        for (int i = 1; i < parts.Length - 1; i++)
        {
            var parentDomain = string.Join('.', parts[i..]);
            if (KnownBlossomServers.Contains(parentDomain))
                return true;
        }

        return false;
    }

    public async Task<string?> ValidateUrlAsync(string url, CancellationToken ct = default)
    {
        _logger.LogDebug("ValidateUrl: checking {Url}", SanitizeUrlForLog(url));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Invalid URL format.";

        // HTTPS only
        if (uri.Scheme != "https")
        {
            _logger.LogWarning("ValidateUrl: rejected non-HTTPS URL: {Scheme}", uri.Scheme);
            return "Only HTTPS URLs are allowed for security.";
        }

        // Resolve DNS and check for private IPs
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrReservedIp(addr))
                {
                    _logger.LogWarning("ValidateUrl: rejected private/reserved IP {Ip} for host {Host}",
                        addr, uri.Host);
                    return $"URL resolves to a private/reserved IP address ({addr}). This could be an SSRF attack.";
                }
            }
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "ValidateUrl: DNS resolution failed for {Host}", uri.Host);
            return $"Cannot resolve hostname: {uri.Host}";
        }

        _logger.LogDebug("ValidateUrl: {Url} passed validation", SanitizeUrlForLog(url));
        return null;
    }

    /// <summary>
    /// Checks if an IP address is in a private or reserved range.
    /// Blocks: 10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x, ::1, fc00::/7, fe80::/10
    /// </summary>
    private static bool IsPrivateOrReservedIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 127.0.0.0/8
            if (bytes[0] == 127)
                return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;

            // 0.0.0.0/8
            if (bytes[0] == 0)
                return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 (loopback) — already handled by IsLoopback above

            // fc00::/7 (unique local)
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;

            // fe80::/10 (link-local)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;

            // :: (unspecified)
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
                return true;
        }

        return false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private static string SanitizeUrlForLog(string url)
    {
        // Only log the host + path, not query strings which might contain tokens
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        return "(invalid URL)";
    }
}
