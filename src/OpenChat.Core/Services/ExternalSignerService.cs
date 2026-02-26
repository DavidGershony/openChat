using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using OpenChat.Core.Crypto;
using OpenChat.Core.Logging;
using System.Security.Cryptography;
using Aes = System.Security.Cryptography.Aes;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;
using SHA256 = System.Security.Cryptography.SHA256;

namespace OpenChat.Core.Services;

/// <summary>
/// NIP-46 (Nostr Connect) implementation for external signers.
/// Supports bunker URLs for signers like Amber, nsecBunker, etc.
/// </summary>
public class ExternalSignerService : IExternalSigner, IDisposable
{
    private readonly ILogger<ExternalSignerService> _logger;
    private readonly Subject<ExternalSignerStatus> _status = new();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string? _relayUrl;
    private string? _remotePubKey;
    private string? _secret;
    private string? _localPrivateKeyHex;
    private string? _localPublicKeyHex;
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private bool _disposed;

    public ExternalSignerService()
    {
        _logger = LoggingConfiguration.CreateLogger<ExternalSignerService>();
        _logger.LogDebug("ExternalSignerService initialized");
    }

    public bool IsConnected { get; private set; }
    public string? PublicKeyHex { get; private set; }
    public string? Npub => PublicKeyHex != null ? Bech32.Encode("npub", Convert.FromHexString(PublicKeyHex)) : null;
    public IObservable<ExternalSignerStatus> Status => _status.AsObservable();

    public async Task<bool> ConnectAsync(string bunkerUrl)
    {
        return await ConnectWithStringAsync(bunkerUrl);
    }

    public async Task<bool> ConnectWithStringAsync(string connectionString)
    {
        _logger.LogInformation("Attempting to connect with external signer using connection string");
        _logger.LogDebug("Connection string length: {Length}", connectionString.Length);

        try
        {
            _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Connecting });

            // Parse the connection string
            if (!ParseConnectionString(connectionString, out var remotePubKey, out var relayUrl, out var secret))
            {
                _logger.LogError("Failed to parse connection string - invalid format");
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "Invalid connection string format"
                });
                return false;
            }

            _logger.LogDebug("Parsed connection string - Relay: {Relay}, RemotePubKey: {PubKey}", relayUrl, remotePubKey?[..16] + "...");

            _remotePubKey = remotePubKey;
            _relayUrl = relayUrl;
            _secret = secret;

            // Generate ephemeral keypair for NIP-46 communication
            GenerateLocalKeyPair();
            _logger.LogDebug("Generated ephemeral keypair for NIP-46 communication");

            // Connect to relay
            _logger.LogInformation("Connecting to relay: {RelayUrl}", _relayUrl);
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri(_relayUrl!), _cts.Token);
            _logger.LogInformation("Successfully connected to relay");

            // Start listening for messages
            _ = Task.Run(() => ListenForMessagesAsync(_cts.Token));
            _logger.LogDebug("Started message listener");

            // Subscribe to responses from the signer
            await SubscribeToSignerAsync();
            _logger.LogDebug("Subscribed to signer responses");

            // Send connect request
            _logger.LogInformation("Sending connect request to signer, waiting for approval...");
            _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.WaitingForApproval });
            var response = await SendRequestAsync("connect", new[] { _localPublicKeyHex!, _secret ?? "" });
            _logger.LogDebug("Received connect response: {Response}", response);

            if (response == "ack")
            {
                // Get public key from signer
                _logger.LogInformation("Connection approved, fetching public key from signer");
                PublicKeyHex = await GetPublicKeyAsync();
                IsConnected = true;
                _logger.LogInformation("Successfully connected to external signer. Public key: {PubKey}", PublicKeyHex[..16] + "...");
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Connected,
                    IsConnected = true,
                    PublicKeyHex = PublicKeyHex
                });
                return true;
            }
            else
            {
                _logger.LogWarning("Connection rejected by signer. Response: {Response}", response);
                _status.OnNext(new ExternalSignerStatus
                {
                    State = ExternalSignerState.Error,
                    Error = "Connection rejected by signer"
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to external signer");
            _status.OnNext(new ExternalSignerStatus
            {
                State = ExternalSignerState.Error,
                Error = ex.Message
            });
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        PublicKeyHex = null;

        _cts?.Cancel();

        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
            catch { }
        }

        _webSocket?.Dispose();
        _webSocket = null;
        _cts?.Dispose();
        _cts = null;

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Disconnected });
    }

    public async Task<string> GetPublicKeyAsync()
    {
        var response = await SendRequestAsync("get_public_key", Array.Empty<string>());
        return response;
    }

    public async Task<string> SignEventAsync(UnsignedNostrEvent unsignedEvent)
    {
        var eventJson = JsonSerializer.Serialize(new
        {
            kind = unsignedEvent.Kind,
            content = unsignedEvent.Content,
            tags = unsignedEvent.Tags,
            created_at = new DateTimeOffset(unsignedEvent.CreatedAt).ToUnixTimeSeconds()
        });

        var response = await SendRequestAsync("sign_event", new[] { eventJson });
        return response;
    }

    public async Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)
    {
        var response = await SendRequestAsync("nip44_encrypt", new[] { recipientPubKey, plaintext });
        return response;
    }

    public async Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)
    {
        var response = await SendRequestAsync("nip44_decrypt", new[] { senderPubKey, ciphertext });
        return response;
    }

    public string GenerateConnectionUri(string relayUrl)
    {
        GenerateLocalKeyPair();
        var secret = GenerateRandomSecret();
        return $"nostrconnect://{_localPublicKeyHex}?relay={Uri.EscapeDataString(relayUrl)}&metadata={Uri.EscapeDataString("{\"name\":\"OpenChat\"}")}";
    }

    private bool ParseConnectionString(string connectionString, out string? remotePubKey, out string? relayUrl, out string? secret)
    {
        remotePubKey = null;
        relayUrl = null;
        secret = null;

        try
        {
            Uri uri;

            // Handle bunker:// URLs
            if (connectionString.StartsWith("bunker://"))
            {
                uri = new Uri(connectionString);
                remotePubKey = uri.Host;
            }
            // Handle nostrconnect:// URLs
            else if (connectionString.StartsWith("nostrconnect://"))
            {
                uri = new Uri(connectionString);
                remotePubKey = uri.Host;
            }
            else
            {
                return false;
            }

            // Parse query parameters
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            relayUrl = query["relay"];
            secret = query["secret"];

            return !string.IsNullOrEmpty(remotePubKey) && !string.IsNullOrEmpty(relayUrl);
        }
        catch
        {
            return false;
        }
    }

    private void GenerateLocalKeyPair()
    {
        var privateKeyBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(privateKeyBytes);

        _localPrivateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

        if (Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) && ecPrivKey != null)
        {
            var pubKey = ecPrivKey.CreateXOnlyPubKey();
            var publicKeyBytes = new byte[32];
            pubKey.WriteToSpan(publicKeyBytes);
            _localPublicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
        }
    }

    private static string GenerateRandomSecret()
    {
        var bytes = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task SubscribeToSignerAsync()
    {
        if (_webSocket == null || _localPublicKeyHex == null) return;

        // Subscribe to kind 24133 events (NIP-46 responses) tagged to our local pubkey
        var subscriptionId = Guid.NewGuid().ToString("N")[..8];
        var filter = new Dictionary<string, object>
        {
            ["kinds"] = new[] { 24133 },
            ["#p"] = new[] { _localPublicKeyHex },
            ["since"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var req = JsonSerializer.Serialize(new object[] { "REQ", subscriptionId, filter });
        _logger.LogDebug("NIP-46 sending REQ: {Req}", req);
        var bytes = Encoding.UTF8.GetBytes(req);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
    }

    private async Task<string> SendRequestAsync(string method, string[] @params)
    {
        if (_webSocket == null || _localPrivateKeyHex == null || _remotePubKey == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new Nip46Request
        {
            Id = requestId,
            Method = method,
            Params = @params
        };

        var requestJson = JsonSerializer.Serialize(request);

        // Encrypt the request using NIP-44 (simplified - using NIP-04 style for now)
        var encryptedContent = EncryptNip04(requestJson, _localPrivateKeyHex, _remotePubKey);

        // Create and sign the event
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tags = new[] { new[] { "p", _remotePubKey } };

        var eventId = ComputeEventId(24133, _localPublicKeyHex!, createdAt, tags, encryptedContent);
        var signature = SignEventId(eventId, _localPrivateKeyHex);

        // Serialize EVENT message with Utf8JsonWriter to ensure correct format
        using var eventMs = new MemoryStream();
        using (var ew = new Utf8JsonWriter(eventMs, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            ew.WriteStartArray();
            ew.WriteStringValue("EVENT");
            ew.WriteStartObject();
            ew.WriteString("id", eventId);
            ew.WriteString("pubkey", _localPublicKeyHex);
            ew.WriteNumber("created_at", createdAt);
            ew.WriteNumber("kind", 24133);
            ew.WritePropertyName("tags");
            ew.WriteStartArray();
            foreach (var tag in tags)
            {
                ew.WriteStartArray();
                foreach (var v in tag)
                    ew.WriteStringValue(v);
                ew.WriteEndArray();
            }
            ew.WriteEndArray();
            ew.WriteString("content", encryptedContent);
            ew.WriteString("sig", signature);
            ew.WriteEndObject();
            ew.WriteEndArray();
        }
        var eventMessage = Encoding.UTF8.GetString(eventMs.ToArray());
        var bytes = Encoding.UTF8.GetBytes(eventMessage);

        _logger.LogDebug("NIP-46 sending EVENT: {Event}", eventMessage.Length > 300 ? eventMessage[..300] + "..." : eventMessage);

        var tcs = new TaskCompletionSource<string>();
        _pendingRequests[requestId] = tcs;

        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);

        // Wait for response with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException("Signer request timed out")));

        return await tcs.Task;
    }

    private async Task ListenForMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();
                    ProcessMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    private void ProcessMessage(string message)
    {
        try
        {
            _logger.LogDebug("NIP-46 relay message: {Message}", message.Length > 500 ? message[..500] + "..." : message);

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
                return;

            var messageType = root[0].GetString();

            if (messageType == "OK" && root.GetArrayLength() >= 4)
            {
                var eventId = root[1].GetString();
                var accepted = root[2].GetBoolean();
                var reason = root.GetArrayLength() > 3 ? root[3].GetString() : "";
                if (accepted)
                    _logger.LogInformation("NIP-46 event {EventId} accepted by relay", eventId?[..16]);
                else
                    _logger.LogError("NIP-46 event {EventId} REJECTED by relay: {Reason}", eventId?[..16], reason);
            }
            else if (messageType == "NOTICE")
            {
                var notice = root[1].GetString();
                _logger.LogWarning("NIP-46 relay NOTICE: {Notice}", notice);
            }
            else if (messageType == "EOSE")
            {
                _logger.LogDebug("NIP-46 end of stored events");
            }
            else if (messageType == "EVENT" && root.GetArrayLength() >= 3)
            {
                var eventObj = root[2];
                var kind = eventObj.GetProperty("kind").GetInt32();
                var senderPubKey = eventObj.GetProperty("pubkey").GetString();
                _logger.LogInformation("NIP-46 received event kind={Kind} from {Sender}", kind, senderPubKey?[..16]);

                if (kind == 24133 && _localPrivateKeyHex != null)
                {
                    var content = eventObj.GetProperty("content").GetString();

                    if (content != null && senderPubKey != null)
                    {
                        // Decrypt the response
                        var decrypted = DecryptNip04(content, _localPrivateKeyHex, senderPubKey);
                        _logger.LogDebug("NIP-46 decrypted response: {Response}", decrypted.Length > 200 ? decrypted[..200] + "..." : decrypted);
                        var response = JsonSerializer.Deserialize<Nip46Response>(decrypted);

                        if (response?.Id != null && _pendingRequests.TryGetValue(response.Id, out var tcs))
                        {
                            _pendingRequests.Remove(response.Id);

                            if (response.Error != null)
                            {
                                _logger.LogWarning("NIP-46 signer returned error: {Error}", response.Error);
                                tcs.TrySetException(new Exception(response.Error));
                            }
                            else
                            {
                                _logger.LogInformation("NIP-46 signer returned result for request {Id}", response.Id[..8]);
                                tcs.TrySetResult(response.Result ?? "");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("NIP-46 received response for unknown request: {Id}", response?.Id);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-46 error processing relay message");
        }
    }

    private static string ComputeEventId(int kind, string pubkey, long createdAt, string[][] tags, string content)
    {
        // Use Utf8JsonWriter to avoid the object[] boxing bug with string[][] tags
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(0);
            writer.WriteStringValue(pubkey);
            writer.WriteNumberValue(createdAt);
            writer.WriteNumberValue(kind);

            // Tags: array of arrays of strings
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var value in tag)
                {
                    writer.WriteStringValue(value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStringValue(content);
            writer.WriteEndArray();
        }

        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SignEventId(string eventId, string privateKeyHex)
    {
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var messageBytes = Convert.FromHexString(eventId);

        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey == null)
        {
            throw new ArgumentException("Invalid private key");
        }

        // Sign with Schnorr signature
        if (!ecPrivKey.TrySignBIP340(messageBytes, null, out var signature))
        {
            throw new InvalidOperationException("Failed to sign event");
        }
        var sigBytes = new byte[64];
        signature.WriteToSpan(sigBytes);
        return Convert.ToHexString(sigBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the NIP-04 ECDH shared secret between a private key and a public key.
    /// Returns the 32-byte x-coordinate of the ECDH shared point.
    /// </summary>
    private static byte[] ComputeNip04SharedSecret(string privateKeyHex, string pubKeyHex)
    {
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var pubKeyBytes = Convert.FromHexString(pubKeyHex);

        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey == null)
            throw new ArgumentException("Invalid private key for NIP-04");

        // Convert x-only pubkey (32 bytes) to compressed pubkey (33 bytes, 0x02 prefix = even y)
        var compressedPubKey = new byte[33];
        compressedPubKey[0] = 0x02;
        Array.Copy(pubKeyBytes, 0, compressedPubKey, 1, 32);

        if (!ECPubKey.TryCreate(compressedPubKey, Context.Instance, out _, out var ecPubKey) || ecPubKey == null)
            throw new ArgumentException("Invalid public key for NIP-04");

        // ECDH: shared point = privkey * pubkey
        var sharedPubKey = ecPubKey.GetSharedPubkey(ecPrivKey);

        // Extract x-coordinate (32 bytes) — this is the NIP-04 shared secret
        var sharedCompressed = new byte[33];
        sharedPubKey.WriteToSpan(compressed: true, sharedCompressed, out _);
        var sharedSecret = new byte[32];
        Array.Copy(sharedCompressed, 1, sharedSecret, 0, 32);

        return sharedSecret;
    }

    /// <summary>
    /// NIP-04 encryption: AES-256-CBC with ECDH shared secret.
    /// Format: base64(ciphertext)?iv=base64(iv)
    /// </summary>
    private static string EncryptNip04(string plaintext, string privateKeyHex, string pubKeyHex)
    {
        var sharedSecret = ComputeNip04SharedSecret(privateKeyHex, pubKeyHex);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(iv);

        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return Convert.ToBase64String(encrypted) + "?iv=" + Convert.ToBase64String(iv);
    }

    /// <summary>
    /// NIP-04 decryption: AES-256-CBC with ECDH shared secret.
    /// </summary>
    private static string DecryptNip04(string ciphertext, string privateKeyHex, string pubKeyHex)
    {
        var parts = ciphertext.Split("?iv=");
        if (parts.Length != 2)
        {
            throw new FormatException("Invalid NIP-04 ciphertext format");
        }

        var encryptedData = Convert.FromBase64String(parts[0]);
        var iv = Convert.FromBase64String(parts[1]);

        var sharedSecret = ComputeNip04SharedSecret(privateKeyHex, pubKeyHex);

        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cts?.Cancel();
        _webSocket?.Dispose();
        _cts?.Dispose();
        _status.Dispose();

        _disposed = true;
    }

    private class Nip46Request
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public string[] Params { get; set; } = Array.Empty<string>();
    }

    private class Nip46Response
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
