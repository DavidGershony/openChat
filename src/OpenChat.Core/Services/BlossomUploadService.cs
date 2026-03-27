using System.Net.Http.Headers;
using System.Text;
using SHA256 = System.Security.Cryptography.SHA256;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Services;

/// <summary>
/// Uploads encrypted blobs to Blossom servers per BUD-02.
/// Auth: NIP-98 kind 24242 signed event in Authorization header.
/// </summary>
public class BlossomUploadService : IMediaUploadService
{
    private readonly ILogger<BlossomUploadService> _logger;
    private readonly HttpClient _httpClient;
    private IExternalSigner? _externalSigner;
    private string? _lastUploadError;

    public string BlossomServerUrl { get; set; } = "https://blossom.primal.net";

    public BlossomUploadService(IExternalSigner? externalSigner = null)
    {
        _logger = LoggingConfiguration.CreateLogger<BlossomUploadService>();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _externalSigner = externalSigner;
        _logger.LogInformation("BlossomUploadService initialized (server: {Server})", BlossomServerUrl);
    }

    public void SetExternalSigner(IExternalSigner? signer)
    {
        _externalSigner = signer;
    }

    public async Task<BlobUploadResult> UploadAsync(byte[] encryptedData, string? privateKeyHex, string? contentType = null, CancellationToken ct = default)
    {
        var sha256Hex = Convert.ToHexString(SHA256.HashData(encryptedData)).ToLowerInvariant();
        _logger.LogInformation("Uploading {Size} bytes to {Server} (sha256: {Hash})",
            encryptedData.Length, BlossomServerUrl, sha256Hex[..16]);

        var url = $"{BlossomServerUrl.TrimEnd('/')}/upload";
        string? lastError = null;

        // Skip anonymous upload when using external signer (auth is always required)
        if (!string.IsNullOrEmpty(privateKeyHex) || _externalSigner?.IsConnected != true)
        {
            var anonResult = await TryUploadAsync(url, encryptedData, sha256Hex, authHeader: null, ct);
            if (anonResult != null) return anonResult;

            lastError = _lastUploadError;
            _logger.LogInformation("Anonymous upload rejected, retrying with NIP-98 auth");
        }

        // Authenticated upload (local key or external signer)
        var authHeader = await BuildAuthHeaderAsync(sha256Hex, privateKeyHex);
        var authResult = await TryUploadAsync(url, encryptedData, sha256Hex, authHeader, ct);
        if (authResult != null) return authResult;

        lastError = _lastUploadError ?? lastError;
        var errorDetail = !string.IsNullOrEmpty(lastError) ? lastError : "server returned an error";
        throw new InvalidOperationException($"Blossom upload failed: {errorDetail}");
    }

    private async Task<BlobUploadResult?> TryUploadAsync(string url, byte[] data, string sha256Hex, string? authHeader, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (authHeader != null)
                request.Headers.TryAddWithoutValidation("Authorization", $"Nostr {authHeader}");

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _lastUploadError = $"{(int)response.StatusCode} {response.StatusCode}: {body}";
                _logger.LogWarning("Blossom upload attempt failed: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Blossom upload succeeded: {Response}", json[..Math.Min(200, json.Length)]);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new BlobUploadResult
            {
                Sha256 = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? sha256Hex : sha256Hex,
                Url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"{BlossomServerUrl}/{sha256Hex}",
                Size = data.Length
            };
        }
        catch (HttpRequestException ex)
        {
            _lastUploadError = $"Connection failed: {ex.Message}";
            _logger.LogWarning(ex, "Blossom upload connection failed to {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Builds a base64-encoded kind 24242 Nostr event for BUD-02 auth.
    /// Tags: [["t","upload"], ["x","sha256hex"], ["expiration","unix"]]
    /// </summary>
    private async Task<string> BuildAuthHeaderAsync(string sha256Hex, string? privateKeyHex)
    {
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tags = new List<List<string>>
        {
            new() { "t", "upload" },
            new() { "x", sha256Hex },
            new() { "expiration", expiration.ToString() }
        };
        var content = "Upload blob";

        if (!string.IsNullOrEmpty(privateKeyHex))
        {
            // Sign locally — handle nsec (bech32) format if needed
            if (privateKeyHex.StartsWith("nsec"))
            {
                var (hexKey, _, _, _) = new NostrService().ImportPrivateKey(privateKeyHex);
                privateKeyHex = hexKey;
            }
            var privBytes = Convert.FromHexString(privateKeyHex);
            var pubBytes = NostrService.DerivePublicKey(privBytes);
            var pubHex = Convert.ToHexString(pubBytes).ToLowerInvariant();

            var serialized = NostrService.SerializeForEventId(pubHex, createdAt, 24242, tags, content);
            var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
            var idHex = Convert.ToHexString(idBytes).ToLowerInvariant();
            var sigBytes = NostrService.SignSchnorr(idBytes, privBytes);
            var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

            var eventJson = BuildEventJson(idHex, pubHex, createdAt, 24242, tags, content, sigHex);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(eventJson));
        }
        else if (_externalSigner?.IsConnected == true)
        {
            // Sign via external signer
            var unsignedEvent = new UnsignedNostrEvent
            {
                Kind = 24242,
                Content = content,
                Tags = tags,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime
            };
            var signedJson = await _externalSigner.SignEventAsync(unsignedEvent);
            _logger.LogInformation("External signer returned auth event ({Length} chars)", signedJson.Length);
            _logger.LogDebug("Signed auth event: {Event}", signedJson.Length > 500 ? signedJson[..500] + "..." : signedJson);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(signedJson));
        }
        else
        {
            throw new InvalidOperationException("Cannot upload to Blossom: no private key and no external signer.");
        }
    }

    private static string BuildEventJson(string id, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("pubkey", pubkey);
            w.WriteNumber("created_at", createdAt);
            w.WriteNumber("kind", kind);
            w.WritePropertyName("tags");
            w.WriteStartArray();
            foreach (var tag in tags)
            {
                w.WriteStartArray();
                foreach (var v in tag) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteString("content", content);
            w.WriteString("sig", sig);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
