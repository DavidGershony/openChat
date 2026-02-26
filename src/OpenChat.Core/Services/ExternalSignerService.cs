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
        _secret = GenerateRandomSecret();
        return $"nostrconnect://{_localPublicKeyHex}?relay={Uri.EscapeDataString(relayUrl)}&secret={_secret}&metadata={Uri.EscapeDataString("{\"name\":\"OpenChat\"}")}";
    }

    public async Task<string> GenerateAndListenForConnectionAsync(string relayUrl)
    {
        _logger.LogInformation("Generating nostrconnect URI and listening on relay: {Relay}", relayUrl);

        var uri = GenerateConnectionUri(relayUrl);
        _relayUrl = relayUrl;

        _logger.LogInformation("Generated nostrconnect URI. Local pubkey: {PubKey}", _localPublicKeyHex);

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.Connecting });

        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(relayUrl), _cts.Token);
        _logger.LogInformation("WebSocket connected to {Relay}. State: {State}", relayUrl, _webSocket.State);

        _ = Task.Run(() => ListenForMessagesAsync(_cts.Token));
        await SubscribeToSignerAsync();
        _logger.LogInformation("Subscribed to kind 24133 events for pubkey {PubKey}. Waiting for signer...", _localPublicKeyHex?[..16]);

        _status.OnNext(new ExternalSignerStatus { State = ExternalSignerState.WaitingForApproval });

        return uri;
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
            ["since"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10
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

    private void HandleIncomingConnect(string senderPubKey, JsonElement root, string? reqId)
    {
        _remotePubKey = senderPubKey;
        PublicKeyHex = senderPubKey;

        // Try to extract the actual signer pubkey from params[0] if available
        if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array && paramsProp.GetArrayLength() > 0)
        {
            var signerPubKey = paramsProp[0].GetString();
            if (!string.IsNullOrEmpty(signerPubKey) && signerPubKey.Length == 64)
                PublicKeyHex = signerPubKey;
        }
        // Or from result field (some signers put pubkey there)
        else if (root.TryGetProperty("result", out var resultProp))
        {
            var resultVal = resultProp.GetString();
            if (!string.IsNullOrEmpty(resultVal) && resultVal.Length == 64)
                PublicKeyHex = resultVal;
        }

        IsConnected = true;

        if (reqId != null)
            _ = SendNip46ResponseAsync(reqId, "ack", senderPubKey);

        _logger.LogInformation("NIP-46 signer connected via nostrconnect. PubKey: {PubKey}", PublicKeyHex[..16]);
        _status.OnNext(new ExternalSignerStatus
        {
            State = ExternalSignerState.Connected,
            IsConnected = true,
            PublicKeyHex = PublicKeyHex
        });
    }

    private async Task SendNip46ResponseAsync(string requestId, string result, string recipientPubKey)
    {
        if (_webSocket == null || _localPrivateKeyHex == null || _localPublicKeyHex == null) return;

        try
        {
            var responseJson = JsonSerializer.Serialize(new { id = requestId, result });
            var encryptedContent = EncryptNip04(responseJson, _localPrivateKeyHex, recipientPubKey);

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tags = new[] { new[] { "p", recipientPubKey } };
            var eventId = ComputeEventId(24133, _localPublicKeyHex, createdAt, tags, encryptedContent);
            var signature = SignEventId(eventId, _localPrivateKeyHex);

            using var ms = new MemoryStream();
            using (var ew = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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
                    foreach (var v in tag) ew.WriteStringValue(v);
                    ew.WriteEndArray();
                }
                ew.WriteEndArray();
                ew.WriteString("content", encryptedContent);
                ew.WriteString("sig", signature);
                ew.WriteEndObject();
                ew.WriteEndArray();
            }

            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ms.ToArray()));
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            _logger.LogDebug("NIP-46 sent ack response for request {Id}", requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NIP-46 failed to send response for request {Id}", requestId);
        }
    }

    private async Task ListenForMessagesAsync(CancellationToken ct)
    {
        _logger.LogInformation("NIP-46 WebSocket listener started. State: {State}", _webSocket?.State);
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("NIP-46 WebSocket closed by relay");
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
                _logger.LogDebug("NIP-46 WebSocket listener cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NIP-46 WebSocket listener error. State: {State}", _webSocket?.State);
                // Continue listening on transient errors
            }
        }

        _logger.LogWarning("NIP-46 WebSocket listener exited. State: {State}, Cancelled: {Cancelled}",
            _webSocket?.State, ct.IsCancellationRequested);
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
                        string decrypted;
                        if (content.Contains("?iv="))
                        {
                            // NIP-04 format
                            decrypted = DecryptNip04(content, _localPrivateKeyHex, senderPubKey);
                            _logger.LogDebug("NIP-46 decrypted (NIP-04): {Message}", decrypted.Length > 200 ? decrypted[..200] + "..." : decrypted);
                        }
                        else
                        {
                            // NIP-44 format
                            decrypted = DecryptNip44(content, _localPrivateKeyHex, senderPubKey);
                            _logger.LogDebug("NIP-46 decrypted (NIP-44): {Message}", decrypted.Length > 200 ? decrypted[..200] + "..." : decrypted);
                        }

                        // Check if this is an incoming request (has "method") or a response
                        using var nip46Doc = JsonDocument.Parse(decrypted);
                        if (nip46Doc.RootElement.TryGetProperty("method", out var methodProp))
                        {
                            // Incoming request from signer (nostrconnect:// flow)
                            var method = methodProp.GetString();
                            var reqId = nip46Doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            _logger.LogInformation("NIP-46 incoming request: method={Method}, id={Id}", method, reqId);

                            if (method == "connect")
                            {
                                HandleIncomingConnect(senderPubKey, nip46Doc.RootElement, reqId);
                            }
                        }
                        else
                        {
                            // Response to a pending request (bunker:// flow)
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
                            else if (!IsConnected)
                            {
                                // Unsolicited response while waiting for connection (nostrconnect:// flow)
                                // Some signers send a response instead of a request
                                _logger.LogInformation("NIP-46 treating unsolicited response as connect from {Sender}", senderPubKey[..16]);
                                HandleIncomingConnect(senderPubKey, nip46Doc.RootElement, response?.Id);
                            }
                            else
                            {
                                _logger.LogWarning("NIP-46 received response for unknown request: {Id}", response?.Id);
                            }
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

    /// <summary>
    /// NIP-44 v2 decryption: HKDF + ChaCha20 + HMAC-SHA256.
    /// </summary>
    private static string DecryptNip44(string base64Payload, string privateKeyHex, string pubKeyHex)
    {
        var payload = Convert.FromBase64String(base64Payload);

        // Payload: version(1) || nonce(32) || ciphertext(variable) || hmac(32)
        if (payload.Length < 99) // 1 + 32 + 2(min padded) + 32(mac) = 67 minimum, but padded min is 32 so 1+32+32+32=97
            throw new FormatException("NIP-44 payload too short");

        if (payload[0] != 2)
            throw new FormatException($"Unsupported NIP-44 version: {payload[0]}");

        var nonce = payload[1..33];
        var ciphertext = payload[33..^32];
        var mac = payload[^32..];

        // Shared secret (same ECDH as NIP-04)
        var sharedSecret = ComputeNip04SharedSecret(privateKeyHex, pubKeyHex);

        // Conversation key = HKDF-Extract(salt="nip44-v2", ikm=shared_secret)
        var salt = Encoding.UTF8.GetBytes("nip44-v2");
        var conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, salt);

        // Message keys = HKDF-Expand(prk=conversation_key, info=nonce, L=76)
        var messageKeys = HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, 76, nonce);
        var chachaKey = messageKeys[..32];
        var chachaNonce = messageKeys[32..44];
        var hmacKey = messageKeys[44..76];

        // Verify HMAC-SHA256(hmac_key, nonce || ciphertext)
        using var hmacSha256 = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var macInput = new byte[nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, macInput, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, macInput, nonce.Length, ciphertext.Length);
        var expectedMac = hmacSha256.ComputeHash(macInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
            throw new CryptographicException("NIP-44 HMAC verification failed");

        // Decrypt with ChaCha20
        var padded = ChaCha20Transform(chachaKey, chachaNonce, ciphertext);

        // Unpad: first 2 bytes are big-endian plaintext length
        var plaintextLen = (padded[0] << 8) | padded[1];
        if (plaintextLen < 1 || plaintextLen > padded.Length - 2)
            throw new FormatException($"Invalid NIP-44 padding length: {plaintextLen}");

        return Encoding.UTF8.GetString(padded, 2, plaintextLen);
    }

    /// <summary>
    /// ChaCha20 stream cipher (RFC 8439). XORs input with keystream.
    /// </summary>
    private static byte[] ChaCha20Transform(byte[] key, byte[] nonce, byte[] input)
    {
        var output = new byte[input.Length];
        var state = new uint[16];
        var working = new uint[16];

        // Initialize state: constants + key + counter + nonce
        state[0] = 0x61707865; // "expa"
        state[1] = 0x3320646e; // "nd 3"
        state[2] = 0x79622d32; // "2-by"
        state[3] = 0x6b206574; // "te k"
        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);
        state[12] = 0; // counter
        for (int i = 0; i < 3; i++)
            state[13 + i] = BitConverter.ToUInt32(nonce, i * 4);

        int offset = 0;
        while (offset < input.Length)
        {
            Array.Copy(state, working, 16);

            // 20 rounds (10 double rounds)
            for (int i = 0; i < 10; i++)
            {
                // Column rounds
                QR(working, 0, 4, 8, 12); QR(working, 1, 5, 9, 13);
                QR(working, 2, 6, 10, 14); QR(working, 3, 7, 11, 15);
                // Diagonal rounds
                QR(working, 0, 5, 10, 15); QR(working, 1, 6, 11, 12);
                QR(working, 2, 7, 8, 13); QR(working, 3, 4, 9, 14);
            }

            // Add initial state and XOR with input
            for (int i = 0; i < 16; i++)
                working[i] += state[i];

            var keystream = new byte[64];
            Buffer.BlockCopy(working, 0, keystream, 0, 64);

            int blockLen = Math.Min(64, input.Length - offset);
            for (int i = 0; i < blockLen; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);

            offset += 64;
            state[12]++; // increment counter
        }

        return output;

        static void QR(uint[] s, int a, int b, int c, int d)
        {
            s[a] += s[b]; s[d] ^= s[a]; s[d] = (s[d] << 16) | (s[d] >> 16);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = (s[b] << 12) | (s[b] >> 20);
            s[a] += s[b]; s[d] ^= s[a]; s[d] = (s[d] << 8) | (s[d] >> 24);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = (s[b] << 7) | (s[b] >> 25);
        }
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
