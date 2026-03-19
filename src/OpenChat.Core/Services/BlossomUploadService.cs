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

    public string BlossomServerUrl { get; set; } = "https://blossom.nostr.build";

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

    public async Task<BlobUploadResult> UploadAsync(byte[] encryptedData, string? privateKeyHex, CancellationToken ct = default)
    {
        var sha256Hex = Convert.ToHexString(SHA256.HashData(encryptedData)).ToLowerInvariant();
        _logger.LogInformation("Uploading {Size} bytes to {Server} (sha256: {Hash})",
            encryptedData.Length, BlossomServerUrl, sha256Hex[..16]);

        // Build NIP-98 authorization: signed kind 24242 event
        var authHeader = await BuildAuthHeaderAsync(sha256Hex, privateKeyHex);

        var url = $"{BlossomServerUrl.TrimEnd('/')}/upload";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ByteArrayContent(encryptedData);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("Authorization", $"Nostr {authHeader}");

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Blossom upload failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Blossom upload failed: {response.StatusCode} — {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("Blossom upload succeeded: {Response}", json[..Math.Min(200, json.Length)]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new BlobUploadResult
        {
            Sha256 = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? sha256Hex : sha256Hex,
            Url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"{BlossomServerUrl}/{sha256Hex}",
            Size = encryptedData.Length
        };
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
            // Sign locally
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
