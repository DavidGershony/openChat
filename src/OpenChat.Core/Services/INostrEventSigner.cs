using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Services;

/// <summary>
/// Abstraction for signing Nostr events (kind 445 group messages, etc.).
/// Decouples MLS event signing from the local Nostr private key so that
/// external signers (Amber / NIP-46) can be used instead.
/// </summary>
public interface INostrEventSigner
{
    /// <summary>
    /// Build and sign a Nostr event, returning the full signed event as UTF-8 JSON bytes.
    /// The output format matches NIP-01: {"id":..,"pubkey":..,"created_at":..,"kind":..,"tags":..,"content":..,"sig":..}
    /// </summary>
    Task<byte[]> SignEventAsync(int kind, string content, List<List<string>> tags, string publicKeyHex);
}

/// <summary>
/// Signs Nostr events locally using a BIP-340 Schnorr private key.
/// Produces byte-identical output to the original ManagedMlsService.BuildSignedNostrEvent.
/// </summary>
public class LocalNostrEventSigner : INostrEventSigner
{
    private readonly ILogger<LocalNostrEventSigner> _logger;
    private readonly string _privateKeyHex;

    public LocalNostrEventSigner(string privateKeyHex)
    {
        _privateKeyHex = privateKeyHex ?? throw new ArgumentNullException(nameof(privateKeyHex));
        _logger = LoggingConfiguration.CreateLogger<LocalNostrEventSigner>();
        _logger.LogInformation("LocalNostrEventSigner created");
    }

    public Task<byte[]> SignEventAsync(int kind, string content, List<List<string>> tags, string publicKeyHex)
    {
        var privateKeyBytes = Convert.FromHexString(_privateKeyHex);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NIP-01: event ID = SHA256([0, pubkey, created_at, kind, tags, content])
        string serializedForId;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteStringValue(publicKeyHex);
                writer.WriteNumberValue(createdAt);
                writer.WriteNumberValue(kind);
                writer.WriteStartArray();
                foreach (var tag in tags)
                {
                    writer.WriteStartArray();
                    foreach (var v in tag) writer.WriteStringValue(v);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStringValue(content);
                writer.WriteEndArray();
            }
            serializedForId = Encoding.UTF8.GetString(stream.ToArray());
        }

        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // BIP-340 Schnorr signature
        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Invalid Nostr private key for signing");
        var sig = ecPrivKey.SignBIP340(eventIdBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Build the event JSON object
        using var eventStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventStream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", publicKeyHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sigHex);
            writer.WriteEndObject();
        }

        _logger.LogDebug("LocalNostrEventSigner: signed kind {Kind} event, id={EventId}", kind, eventId[..Math.Min(16, eventId.Length)]);

        return Task.FromResult(eventStream.ToArray());
    }
}

/// <summary>
/// Signs Nostr events via an external signer (NIP-46 / Amber).
/// Delegates to IExternalSigner.SignEventAsync which returns signed event JSON.
/// </summary>
public class ExternalNostrEventSigner : INostrEventSigner
{
    private readonly ILogger<ExternalNostrEventSigner> _logger;
    private readonly IExternalSigner _signer;

    public ExternalNostrEventSigner(IExternalSigner signer)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = LoggingConfiguration.CreateLogger<ExternalNostrEventSigner>();
        _logger.LogInformation("ExternalNostrEventSigner created (signer connected={IsConnected})", signer.IsConnected);
    }

    public async Task<byte[]> SignEventAsync(int kind, string content, List<List<string>> tags, string publicKeyHex)
    {
        _logger.LogDebug("ExternalNostrEventSigner: requesting signature for kind {Kind} event", kind);

        var unsignedEvent = new UnsignedNostrEvent
        {
            Kind = kind,
            Content = content,
            Tags = tags,
            CreatedAt = DateTime.UtcNow
        };

        var signedJson = await _signer.SignEventAsync(unsignedEvent);

        _logger.LogDebug("ExternalNostrEventSigner: received signed event ({Len} chars)", signedJson.Length);

        return Encoding.UTF8.GetBytes(signedJson);
    }
}
