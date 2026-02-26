using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using OpenChat.Core.Crypto;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

public class NostrService : INostrService, IDisposable
{
    private readonly ILogger<NostrService> _logger;
    private readonly Subject<NostrConnectionStatus> _connectionStatus = new();
    private readonly Subject<NostrEventReceived> _events = new();
    private readonly Subject<MarmotWelcomeEvent> _welcomeMessages = new();
    private readonly Subject<MarmotGroupMessageEvent> _groupMessages = new();
    private readonly Dictionary<string, IDisposable> _subscriptions = new();
    private readonly List<string> _connectedRelays = new();
    private readonly ConcurrentDictionary<string, ClientWebSocket> _relayConnections = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _relayListeners = new();
    private readonly HashSet<string> _subscribedGroupIds = new();
    private string? _subscribedUserPubKey;
    private bool _disposed;

    public NostrService()
    {
        _logger = LoggingConfiguration.CreateLogger<NostrService>();
        _logger.LogDebug("NostrService initialized");
    }

    public IObservable<NostrConnectionStatus> ConnectionStatus => _connectionStatus.AsObservable();
    public IObservable<NostrEventReceived> Events => _events.AsObservable();
    public IObservable<MarmotWelcomeEvent> WelcomeMessages => _welcomeMessages.AsObservable();
    public IObservable<MarmotGroupMessageEvent> GroupMessages => _groupMessages.AsObservable();

    public async Task ConnectAsync(string relayUrl)
    {
        await ConnectAsync(new[] { relayUrl });
    }

    public async Task ConnectAsync(IEnumerable<string> relayUrls)
    {
        var relayList = relayUrls.ToList();
        _logger.LogInformation("Connecting to {Count} relays: {Relays}", relayList.Count, string.Join(", ", relayList));

        var connectionTasks = relayList.Select(ConnectToRelayAsync);
        await Task.WhenAll(connectionTasks);
    }

    private async Task ConnectToRelayAsync(string relayUrl)
    {
        try
        {
            _logger.LogDebug("Attempting connection to relay: {RelayUrl}", relayUrl);

            var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource();

            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            _relayConnections[relayUrl] = ws;
            _relayListeners[relayUrl] = cts;
            _connectedRelays.Add(relayUrl);

            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = true
            });

            _logger.LogInformation("Successfully connected to relay: {RelayUrl}", relayUrl);

            // Start listening for events in background
            _ = ListenToRelayAsync(relayUrl, ws, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to relay: {RelayUrl}", relayUrl);
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = false,
                Error = ex.Message
            });
        }
    }

    private async Task ListenToRelayAsync(string relayUrl, ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Relay {RelayUrl} closed connection", relayUrl);
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessRelayMessage(relayUrl, message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Relay listener cancelled for {RelayUrl}", relayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listening to relay {RelayUrl}", relayUrl);
        }
        finally
        {
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = relayUrl,
                IsConnected = false
            });
        }
    }

    private void ProcessRelayMessage(string relayUrl, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                return;

            var messageType = root[0].GetString();

            if (messageType == "EVENT" && root.GetArrayLength() >= 3)
            {
                var eventData = root[2];
                var nostrEvent = ParseNostrEvent(eventData, relayUrl);
                if (nostrEvent != null)
                {
                    _logger.LogDebug("Received event kind {Kind} from {RelayUrl}", nostrEvent.Kind, relayUrl);
                    _events.OnNext(nostrEvent);

                    // Route Marmot-specific events
                    if (nostrEvent.Kind == 444)
                    {
                        ProcessWelcomeEvent(nostrEvent);
                    }
                    else if (nostrEvent.Kind == 445)
                    {
                        ProcessGroupMessageEvent(nostrEvent);
                    }
                }
            }
            else if (messageType == "OK" && root.GetArrayLength() >= 3)
            {
                var eventId = root[1].GetString();
                var accepted = root[2].GetBoolean();
                var reason = root.GetArrayLength() >= 4 ? root[3].GetString() : null;
                if (accepted)
                {
                    _logger.LogDebug("Event {EventId} accepted by {RelayUrl}", eventId, relayUrl);
                }
                else
                {
                    _logger.LogWarning("Event {EventId} rejected by {RelayUrl}: {Reason}",
                        eventId, relayUrl, reason ?? "no reason given");
                }
            }
            else if (messageType == "NOTICE")
            {
                var notice = root.GetArrayLength() >= 2 ? root[1].GetString() : "Unknown notice";
                _logger.LogWarning("Notice from {RelayUrl}: {Notice}", relayUrl, notice);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse message from {RelayUrl}", relayUrl);
        }
    }

    private void ProcessWelcomeEvent(NostrEventReceived nostrEvent)
    {
        try
        {
            // Extract recipient from p-tag
            var recipientPubKey = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "p")?[1];

            // Extract KeyPackage event ID from e-tag (MIP-02 requirement)
            var keyPackageEventId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "e")?[1];

            // Extract group ID from h-tag if present (MIP-03 uses 'h' tag)
            var groupId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "h")?[1];

            // Fallback to 'g' tag for backwards compatibility
            if (string.IsNullOrEmpty(groupId))
            {
                groupId = nostrEvent.Tags
                    .FirstOrDefault(t => t.Count >= 2 && t[0] == "g")?[1];
            }

            // Extract relay hints from relays tag
            var relaysTag = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "relays");
            var relayHints = relaysTag?.Skip(1).ToList() ?? new List<string>();

            var welcomeEvent = new MarmotWelcomeEvent
            {
                EventId = nostrEvent.EventId,
                SenderPublicKey = nostrEvent.PublicKey,
                RecipientPublicKey = recipientPubKey ?? "",
                WelcomeData = nostrEvent.Content,
                GroupId = groupId,
                KeyPackageEventId = keyPackageEventId,
                RelayHints = relayHints,
                CreatedAt = nostrEvent.CreatedAt,
                RelayUrl = nostrEvent.RelayUrl
            };

            _logger.LogInformation("Received Welcome message from {Sender} for {Recipient} (KeyPackage: {KpId})",
                welcomeEvent.SenderPublicKey[..Math.Min(16, welcomeEvent.SenderPublicKey.Length)] + "...",
                welcomeEvent.RecipientPublicKey[..Math.Min(16, welcomeEvent.RecipientPublicKey.Length)] + "...",
                keyPackageEventId?[..Math.Min(16, keyPackageEventId.Length)] ?? "none");

            _welcomeMessages.OnNext(welcomeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Welcome event {EventId}", nostrEvent.EventId);
        }
    }

    private void ProcessGroupMessageEvent(NostrEventReceived nostrEvent)
    {
        try
        {
            // Extract group ID from h-tag (MIP-03 uses 'h' tag, not 'g')
            var groupId = nostrEvent.Tags
                .FirstOrDefault(t => t.Count >= 2 && t[0] == "h")?[1] ?? "";

            // Fallback to 'g' tag for backwards compatibility
            if (string.IsNullOrEmpty(groupId))
            {
                groupId = nostrEvent.Tags
                    .FirstOrDefault(t => t.Count >= 2 && t[0] == "g")?[1] ?? "";
            }

            // Extract epoch from epoch tag if present
            ulong? epoch = null;
            var epochTag = nostrEvent.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "epoch");
            if (epochTag != null && ulong.TryParse(epochTag[1], out var parsedEpoch))
            {
                epoch = parsedEpoch;
            }

            var groupMessageEvent = new MarmotGroupMessageEvent
            {
                EventId = nostrEvent.EventId,
                SenderPublicKey = nostrEvent.PublicKey,
                GroupId = groupId,
                EncryptedData = nostrEvent.Content,
                Epoch = epoch,
                CreatedAt = nostrEvent.CreatedAt,
                RelayUrl = nostrEvent.RelayUrl
            };

            _logger.LogDebug("Received Group message for group {GroupId} from {Sender}",
                groupId[..Math.Min(16, groupId.Length)] + "...",
                groupMessageEvent.SenderPublicKey[..Math.Min(16, groupMessageEvent.SenderPublicKey.Length)] + "...");

            _groupMessages.OnNext(groupMessageEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Group message event {EventId}", nostrEvent.EventId);
        }
    }

    private NostrEventReceived? ParseNostrEvent(JsonElement eventData, string relayUrl)
    {
        try
        {
            var kind = eventData.GetProperty("kind").GetInt32();
            var id = eventData.GetProperty("id").GetString() ?? "";
            var pubkey = eventData.GetProperty("pubkey").GetString() ?? "";
            var content = eventData.GetProperty("content").GetString() ?? "";
            var createdAt = eventData.GetProperty("created_at").GetInt64();

            var tags = new List<List<string>>();
            if (eventData.TryGetProperty("tags", out var tagsElement))
            {
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    var tagList = new List<string>();
                    foreach (var item in tag.EnumerateArray())
                    {
                        tagList.Add(item.GetString() ?? "");
                    }
                    tags.Add(tagList);
                }
            }

            return new NostrEventReceived
            {
                Kind = kind,
                EventId = id,
                PublicKey = pubkey,
                Content = content,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
                Tags = tags,
                RelayUrl = relayUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Nostr event from {RelayUrl}", relayUrl);
            return null;
        }
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting from all relays");

        // Cancel all listeners
        foreach (var (relayUrl, cts) in _relayListeners)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling listener for {RelayUrl}", relayUrl);
            }
        }
        _relayListeners.Clear();

        // Close all WebSocket connections
        foreach (var (relayUrl, ws) in _relayConnections)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                }
                ws.Dispose();

                _connectionStatus.OnNext(new NostrConnectionStatus
                {
                    RelayUrl = relayUrl,
                    IsConnected = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing connection to {RelayUrl}", relayUrl);
            }
        }
        _relayConnections.Clear();
        _connectedRelays.Clear();
    }

    public async Task ReconnectRelayAsync(string relayUrl)
    {
        _logger.LogInformation("Reconnecting to relay: {RelayUrl}", relayUrl);
        await DisconnectSingleRelayAsync(relayUrl);
        await ConnectToRelayAsync(relayUrl);
    }

    public async Task DisconnectRelayAsync(string relayUrl)
    {
        _logger.LogInformation("Disconnecting from relay: {RelayUrl}", relayUrl);
        await DisconnectSingleRelayAsync(relayUrl);
        _connectionStatus.OnNext(new NostrConnectionStatus
        {
            RelayUrl = relayUrl,
            IsConnected = false,
            Removed = true
        });
    }

    private async Task DisconnectSingleRelayAsync(string relayUrl)
    {
        if (_relayListeners.TryRemove(relayUrl, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        if (_relayConnections.TryRemove(relayUrl, out var ws))
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                ws.Dispose();
            }
            catch { }
        }
        _connectedRelays.Remove(relayUrl);
    }

    public (string privateKeyHex, string publicKeyHex, string nsec, string npub) GenerateKeyPair()
    {
        _logger.LogInformation("Generating new Nostr keypair");

        try
        {
            // Generate a random 32-byte private key
            var privateKeyBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(privateKeyBytes);
            _logger.LogDebug("Generated random 32-byte private key");

            var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

            // Derive public key using secp256k1
            var publicKeyBytes = DerivePublicKey(privateKeyBytes);
            var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
            _logger.LogDebug("Derived public key using secp256k1");

            // Generate bech32 encoded versions (NIP-19)
            var nsec = Bech32.Encode("nsec", privateKeyBytes);
            var npub = Bech32.Encode("npub", publicKeyBytes);
            _logger.LogDebug("Encoded keys to bech32 format - nsec length: {NsecLen}, npub length: {NpubLen}", nsec.Length, npub.Length);

            _logger.LogInformation("Successfully generated keypair. Public key (npub): {Npub}", npub);
            return (privateKeyHex, publicKeyHex, nsec, npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate keypair");
            throw;
        }
    }

    public (string privateKeyHex, string publicKeyHex, string nsec, string npub) ImportPrivateKey(string privateKey)
    {
        var keyFormat = privateKey.StartsWith("nsec") ? "nsec (bech32)" : "hex";
        _logger.LogInformation("Importing private key in {Format} format, length: {Length}", keyFormat, privateKey.Length);

        try
        {
            byte[] privateKeyBytes;

            if (privateKey.StartsWith("nsec"))
            {
                _logger.LogDebug("Decoding bech32 nsec key");
                privateKeyBytes = Bech32.Decode(privateKey, out var hrp);
                _logger.LogDebug("Decoded bech32 key with HRP: {Hrp}, resulting bytes: {ByteCount}", hrp, privateKeyBytes.Length);
            }
            else
            {
                _logger.LogDebug("Parsing hex private key");
                privateKeyBytes = Convert.FromHexString(privateKey);
                _logger.LogDebug("Parsed hex key, resulting bytes: {ByteCount}", privateKeyBytes.Length);
            }

            if (privateKeyBytes.Length != 32)
            {
                _logger.LogError("Invalid private key length: {Length} bytes (expected 32)", privateKeyBytes.Length);
                throw new ArgumentException($"Private key must be 32 bytes, got {privateKeyBytes.Length}");
            }

            var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();

            _logger.LogDebug("Deriving public key from private key");
            var publicKeyBytes = DerivePublicKey(privateKeyBytes);
            var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();

            var nsec = Bech32.Encode("nsec", privateKeyBytes);
            var npub = Bech32.Encode("npub", publicKeyBytes);

            _logger.LogInformation("Successfully imported private key. Public key (npub): {Npub}", npub);
            return (privateKeyHex, publicKeyHex, nsec, npub);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to parse private key - invalid format");
            throw new ArgumentException($"Invalid private key format: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogError(ex, "Unexpected error importing private key");
            throw;
        }
    }

    public async Task SubscribeAsync(string subscriptionId, NostrFilter filter)
    {
        // TODO: Integrate with Blockcore nostr-client
        // Build REQ message and send to connected relays
        await Task.CompletedTask;
    }

    public async Task UnsubscribeAsync(string subscriptionId)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            subscription.Dispose();
            _subscriptions.Remove(subscriptionId);
        }
        await Task.CompletedTask;
    }

    public async Task SubscribeToWelcomesAsync(string publicKeyHex)
    {
        _logger.LogInformation("Subscribing to Welcome messages for {PubKey}",
            publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        _subscribedUserPubKey = publicKeyHex;

        // Build REQ for kind 444 events where p-tag matches our pubkey
        var subId = $"welcome_{Guid.NewGuid():N}"[..16];

        // Create filter: {"kinds": [444], "#p": [publicKeyHex]}
        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 444 } },
            { "#p", new[] { publicKeyHex } }
        };

        var reqMessage = JsonSerializer.Serialize(new object[] { "REQ", subId, filter });
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);

        _logger.LogDebug("Sending Welcome subscription REQ: {Message}", reqMessage);

        // Send to all connected relays
        foreach (var (relayUrl, ws) in _relayConnections)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogDebug("Sent Welcome subscription to {RelayUrl}", relayUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send Welcome subscription to {RelayUrl}", relayUrl);
                }
            }
        }
    }

    public async Task SubscribeToGroupMessagesAsync(IEnumerable<string> groupIds)
    {
        var groupList = groupIds.ToList();
        if (groupList.Count == 0)
        {
            _logger.LogDebug("No groups to subscribe to");
            return;
        }

        _logger.LogInformation("Subscribing to Group messages for {Count} groups", groupList.Count);

        foreach (var groupId in groupList)
        {
            _subscribedGroupIds.Add(groupId);
        }

        // Build REQ for kind 445 events with h-tags matching our groups (MIP-03 uses 'h' tag)
        var subId = $"group_{Guid.NewGuid():N}"[..16];

        // Create filter: {"kinds": [445], "#h": [groupId1, groupId2, ...]}
        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 445 } },
            { "#h", groupList.ToArray() }
        };

        var reqMessage = JsonSerializer.Serialize(new object[] { "REQ", subId, filter });
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);

        _logger.LogDebug("Sending Group subscription REQ: {Message}", reqMessage);

        // Send to all connected relays
        foreach (var (relayUrl, ws) in _relayConnections)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogDebug("Sent Group subscription to {RelayUrl}", relayUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send Group subscription to {RelayUrl}", relayUrl);
                }
            }
        }
    }

    public async Task<string> PublishKeyPackageAsync(byte[] keyPackageData, string privateKeyHex, List<List<string>>? mdkTags = null)
    {
        // Create kind 443 event (MIP-00)
        var relayUrls = _connectedRelays.Count > 0
            ? _connectedRelays.ToList()
            : new List<string> { "wss://relay.damus.io", "wss://nos.lol" };

        List<List<string>> tags;

        if (mdkTags != null && mdkTags.Count > 0)
        {
            // Use MDK-provided tags (these come from the Rust library and are MIP-00 compliant)
            // Filter out the "-" tag (NIP-70 protected event marker) which causes relays
            // to reject the event when NIP-42 authentication is not used
            tags = mdkTags.Where(t => !(t.Count == 1 && t[0] == "-")).ToList();
            _logger.LogInformation("Using {Count} MDK-provided tags for KeyPackage (filtered from {Original})",
                tags.Count, mdkTags.Count);

            // Log the tags for debugging
            foreach (var tag in tags)
            {
                _logger.LogDebug("MDK Tag: [{Tags}]", string.Join(", ", tag));
            }
        }
        else
        {
            // Fallback to hardcoded tags if MDK doesn't provide them
            _logger.LogWarning("No MDK tags provided, using fallback hardcoded tags");
            tags = new List<List<string>>
            {
                new() { "encoding", "base64" },
                new() { "mls_protocol_version", "1.0" },
                new() { "mls_ciphersuite", "0x0001" },  // MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519
                new() { "mls_extensions", "0xf2ee", "0x000a" },  // marmot_group_data, last_resort
            };

            // Add relays tag with all connected relay URLs
            var relaysTag = new List<string> { "relays" };
            relaysTag.AddRange(relayUrls);
            tags.Add(relaysTag);
        }

        var eventId = await PublishEventAsync(443, Convert.ToBase64String(keyPackageData), tags, privateKeyHex);
        return eventId;
    }

    public async Task<string> PublishWelcomeAsync(byte[] welcomeData, string recipientPublicKey, string privateKeyHex, string? keyPackageEventId = null)
    {
        // Create kind 444 event (MIP-02) with required tags
        var relayUrls = _connectedRelays.Count > 0
            ? _connectedRelays.ToList()
            : new List<string> { "wss://relay.damus.io", "wss://nos.lol" };

        var tags = new List<List<string>>
        {
            new() { "p", recipientPublicKey },
            new() { "encoding", "base64" }
        };

        // Add KeyPackage event ID reference (required per MIP-02)
        if (!string.IsNullOrEmpty(keyPackageEventId))
        {
            tags.Add(new List<string> { "e", keyPackageEventId });
        }

        // Add relays tag for locating Group Events
        var relaysTag = new List<string> { "relays" };
        relaysTag.AddRange(relayUrls);
        tags.Add(relaysTag);

        var eventId = await PublishEventAsync(444, Convert.ToBase64String(welcomeData), tags, privateKeyHex);
        return eventId;
    }

    public async Task<string> PublishCommitAsync(byte[] commitData, string groupId, string privateKeyHex)
    {
        // Create kind 445 event for commit/evolution messages (MIP-03)
        var tags = new List<List<string>>
        {
            new() { "h", groupId }  // MIP-03 uses 'h' tag for group ID
        };

        var eventId = await PublishEventAsync(445, Convert.ToBase64String(commitData), tags, privateKeyHex);
        return eventId;
    }

    public async Task<string> PublishGroupMessageAsync(byte[] encryptedData, string groupId, string privateKeyHex)
    {
        // Create kind 445 event (MIP-03) - uses 'h' tag per protocol spec
        var tags = new List<List<string>>
        {
            new() { "h", groupId }  // MIP-03 uses 'h' tag for group ID, not 'g'
        };
        var eventId = await PublishEventAsync(445, Convert.ToBase64String(encryptedData), tags, privateKeyHex);
        return eventId;
    }

    public async Task<IEnumerable<KeyPackage>> FetchKeyPackagesAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching KeyPackages for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        var keyPackages = new List<KeyPackage>();

        // Try each connected relay
        var relaysToTry = _connectedRelays.Count > 0
            ? _connectedRelays.ToList()
            : new List<string> { "wss://relay.damus.io", "wss://nos.lol", "wss://relay.nostr.band" };

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var packages = await FetchKeyPackagesFromRelayAsync(relayUrl, publicKeyHex);
                keyPackages.AddRange(packages);

                if (keyPackages.Count > 0)
                {
                    _logger.LogInformation("Found {Count} KeyPackages from {Relay}", keyPackages.Count, relayUrl);
                    break; // Got what we need
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch KeyPackages from relay {Relay}", relayUrl);
            }
        }

        return keyPackages;
    }

    private async Task<List<KeyPackage>> FetchKeyPackagesFromRelayAsync(string relayUrl, string publicKeyHex)
    {
        var keyPackages = new List<KeyPackage>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = new ClientWebSocket();

        _logger.LogDebug("Connecting to relay {Relay} for KeyPackage fetch", relayUrl);
        await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

        // Generate a unique subscription ID
        var subId = $"kp_{Guid.NewGuid():N}"[..16];

        // Build REQ message: ["REQ", subId, {"kinds": [443], "authors": [pubkey], "limit": 5}]
        var filter = new
        {
            kinds = new[] { 443 },
            authors = new[] { publicKeyHex },
            limit = 5
        };
        var reqMessage = JsonSerializer.Serialize(new object[] { "REQ", subId, filter });

        _logger.LogDebug("Sending REQ for KeyPackages: {Message}", reqMessage);
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);
        await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, cts.Token);

        // Receive responses
        var buffer = new byte[65536];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                    continue;

                var messageType = root[0].GetString();

                if (messageType == "EVENT" && root.GetArrayLength() >= 3)
                {
                    var eventData = root[2];
                    var kind = eventData.GetProperty("kind").GetInt32();

                    if (kind == 443)
                    {
                        var content = eventData.GetProperty("content").GetString();
                        var eventId = eventData.GetProperty("id").GetString();
                        var createdAt = eventData.GetProperty("created_at").GetInt64();

                        if (!string.IsNullOrEmpty(content))
                        {
                            try
                            {
                                // Validate MIP-00 required tags before accepting the key package
                                var tags = eventData.GetProperty("tags");
                                bool hasEncoding = false, hasCiphersuite = false, hasProtocolVersion = false;
                                ushort ciphersuiteId = 0x0001;

                                foreach (var tag in tags.EnumerateArray())
                                {
                                    if (tag.GetArrayLength() < 2) continue;
                                    var tagName = tag[0].GetString();
                                    var tagValue = tag[1].GetString();

                                    switch (tagName)
                                    {
                                        case "encoding" when tagValue is "base64" or "mls-base64":
                                            hasEncoding = true;
                                            break;
                                        case "mls_ciphersuite":
                                            hasCiphersuite = true;
                                            if (tagValue != null && tagValue.StartsWith("0x"))
                                            {
                                                if (ushort.TryParse(tagValue[2..], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                                                    ciphersuiteId = parsed;
                                            }
                                            break;
                                        case "ciphersuite":
                                            hasCiphersuite = true;
                                            if (tagValue != null && ushort.TryParse(tagValue, out var csId))
                                                ciphersuiteId = csId;
                                            break;
                                        case "mls_protocol_version":
                                        case "protocol_version":
                                            hasProtocolVersion = true;
                                            break;
                                    }
                                }

                                if (!hasEncoding || !hasCiphersuite || !hasProtocolVersion)
                                {
                                    _logger.LogWarning(
                                        "KeyPackage event {EventId} missing required MIP-00 tags (encoding={Enc}, ciphersuite={Cs}, version={Ver}), skipping",
                                        eventId, hasEncoding, hasCiphersuite, hasProtocolVersion);
                                    continue;
                                }

                                var keyPackageData = Convert.FromBase64String(content);

                                // Basic sanity check: MLS KeyPackages are typically > 100 bytes
                                if (keyPackageData.Length < 64)
                                {
                                    _logger.LogWarning(
                                        "KeyPackage event {EventId} content too short ({Length} bytes), likely invalid",
                                        eventId, keyPackageData.Length);
                                    continue;
                                }

                                // Store the full event JSON for MLS processing
                                var eventJson = eventData.GetRawText();
                                var keyPackage = new KeyPackage
                                {
                                    Id = eventId ?? Guid.NewGuid().ToString(),
                                    OwnerPublicKey = publicKeyHex,
                                    Data = keyPackageData,
                                    EventJson = eventJson,
                                    NostrEventId = eventId,
                                    CiphersuiteId = ciphersuiteId,
                                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
                                    ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime.AddDays(30),
                                    RelayUrls = new List<string> { relayUrl }
                                };
                                keyPackages.Add(keyPackage);
                                _logger.LogDebug("Found valid KeyPackage {EventId} from {Relay} (ciphersuite=0x{Cs:x4}, {Len} bytes)",
                                    eventId, relayUrl, ciphersuiteId, keyPackageData.Length);
                            }
                            catch (FormatException)
                            {
                                _logger.LogWarning("Invalid base64 content in KeyPackage event {EventId}", eventId);
                            }
                        }
                    }
                }
                else if (messageType == "EOSE")
                {
                    _logger.LogDebug("Received EOSE for KeyPackage subscription {SubId}", subId);
                    break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse relay message");
            }
        }

        // Send CLOSE message
        try
        {
            var closeMessage = JsonSerializer.Serialize(new object[] { "CLOSE", subId });
            var closeBytes = Encoding.UTF8.GetBytes(closeMessage);
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(closeBytes), WebSocketMessageType.Text, true, cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            }
        }
        catch
        {
            // Ignore close errors
        }

        return keyPackages;
    }

    public async Task<IEnumerable<NostrEventReceived>> FetchWelcomeEventsAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching Welcome events for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        var allEvents = new List<NostrEventReceived>();

        var relaysToTry = _connectedRelays.Count > 0
            ? _connectedRelays.ToList()
            : new List<string> { "wss://relay.damus.io", "wss://nos.lol", "wss://relay.nostr.band" };

        foreach (var relayUrl in relaysToTry)
        {
            try
            {
                var events = await FetchWelcomeEventsFromRelayAsync(relayUrl, publicKeyHex);
                _logger.LogInformation("Found {Count} Welcome event(s) from {Relay}", events.Count, relayUrl);
                allEvents.AddRange(events);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Welcome events from relay {Relay}", relayUrl);
            }
        }

        // Deduplicate by EventId
        var unique = allEvents.GroupBy(e => e.EventId).Select(g => g.First()).ToList();
        _logger.LogInformation("Total unique Welcome events found: {Count}", unique.Count);
        return unique;
    }

    private async Task<List<NostrEventReceived>> FetchWelcomeEventsFromRelayAsync(string relayUrl, string publicKeyHex)
    {
        var events = new List<NostrEventReceived>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = new ClientWebSocket();

        _logger.LogDebug("Connecting to relay {Relay} for Welcome fetch", relayUrl);
        await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

        var subId = $"wf_{Guid.NewGuid():N}"[..16];

        // REQ for kind 444 events where p-tag matches our pubkey
        var filter = new Dictionary<string, object>
        {
            { "kinds", new[] { 444 } },
            { "#p", new[] { publicKeyHex } },
            { "limit", 50 }
        };
        var reqMessage = JsonSerializer.Serialize(new object[] { "REQ", subId, filter });

        _logger.LogDebug("Sending Welcome fetch REQ: {Message}", reqMessage);
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);
        await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[65536];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                    continue;

                var messageType = root[0].GetString();

                if (messageType == "EVENT" && root.GetArrayLength() >= 3)
                {
                    var eventData = root[2];
                    var nostrEvent = ParseNostrEvent(eventData, relayUrl);
                    if (nostrEvent != null && nostrEvent.Kind == 444)
                    {
                        _logger.LogDebug("Found Welcome event {EventId} from {Sender}",
                            nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
                            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);
                        events.Add(nostrEvent);
                    }
                }
                else if (messageType == "EOSE")
                {
                    _logger.LogDebug("Received EOSE for Welcome fetch subscription {SubId}", subId);
                    break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse relay message during Welcome fetch");
            }
        }

        // Close subscription
        try
        {
            var closeMessage = JsonSerializer.Serialize(new object[] { "CLOSE", subId });
            var closeBytes = Encoding.UTF8.GetBytes(closeMessage);
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(closeBytes), WebSocketMessageType.Text, true, cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            }
        }
        catch
        {
            // Ignore close errors
        }

        return events;
    }

    public async Task<UserMetadata?> FetchUserMetadataAsync(string publicKeyHex)
    {
        _logger.LogInformation("Fetching metadata for public key: {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)] + "...");

        try
        {
            var npub = Bech32.Encode("npub", Convert.FromHexString(publicKeyHex));

            // Try each connected relay until we get metadata
            var relaysToTry = _connectedRelays.Count > 0
                ? _connectedRelays.ToList()
                : new List<string> { "wss://relay.damus.io", "wss://nos.lol", "wss://relay.nostr.band" };

            foreach (var relayUrl in relaysToTry)
            {
                try
                {
                    var metadata = await FetchMetadataFromRelayAsync(relayUrl, publicKeyHex, npub);
                    if (metadata != null)
                    {
                        _logger.LogInformation("Fetched metadata for {Npub} from {Relay}: {DisplayName}",
                            npub, relayUrl, metadata.GetDisplayName());
                        return metadata;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch metadata from relay {Relay}", relayUrl);
                }
            }

            // If no metadata found, return basic info
            _logger.LogInformation("No metadata found for {Npub}, returning basic info", npub);
            return new UserMetadata
            {
                PublicKeyHex = publicKeyHex,
                Npub = npub,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch metadata for {PubKey}", publicKeyHex);
            return null;
        }
    }

    private async Task<UserMetadata?> FetchMetadataFromRelayAsync(string relayUrl, string publicKeyHex, string npub)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = new ClientWebSocket();

        _logger.LogDebug("Connecting to relay {Relay} for metadata fetch", relayUrl);
        await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

        // Generate a unique subscription ID
        var subId = $"meta_{Guid.NewGuid():N}"[..16];

        // Build REQ message: ["REQ", subId, {"kinds": [0], "authors": [pubkey], "limit": 1}]
        var filter = new
        {
            kinds = new[] { 0 },
            authors = new[] { publicKeyHex },
            limit = 1
        };
        var reqMessage = JsonSerializer.Serialize(new object[] { "REQ", subId, filter });

        _logger.LogDebug("Sending REQ: {Message}", reqMessage);
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);
        await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, cts.Token);

        // Receive responses
        var buffer = new byte[16384];
        UserMetadata? metadata = null;

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _logger.LogDebug("Received from relay: {Message}", message[..Math.Min(200, message.Length)]);

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                    continue;

                var messageType = root[0].GetString();

                if (messageType == "EVENT" && root.GetArrayLength() >= 3)
                {
                    var eventData = root[2];
                    var kind = eventData.GetProperty("kind").GetInt32();

                    if (kind == 0)
                    {
                        var content = eventData.GetProperty("content").GetString();
                        var createdAt = eventData.GetProperty("created_at").GetInt64();

                        if (!string.IsNullOrEmpty(content))
                        {
                            metadata = ParseMetadataContent(content, publicKeyHex, npub);
                            if (metadata != null)
                            {
                                metadata.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime;
                            }
                        }
                    }
                }
                else if (messageType == "EOSE")
                {
                    // End of stored events - we're done
                    _logger.LogDebug("Received EOSE for subscription {SubId}", subId);
                    break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse relay message");
            }
        }

        // Send CLOSE message
        try
        {
            var closeMessage = JsonSerializer.Serialize(new object[] { "CLOSE", subId });
            var closeBytes = Encoding.UTF8.GetBytes(closeMessage);
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(closeBytes), WebSocketMessageType.Text, true, cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            }
        }
        catch
        {
            // Ignore close errors
        }

        return metadata;
    }

    private UserMetadata? ParseMetadataContent(string content, string publicKeyHex, string npub)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var metadata = new UserMetadata
            {
                PublicKeyHex = publicKeyHex,
                Npub = npub
            };

            if (root.TryGetProperty("name", out var name))
                metadata.Name = name.GetString();

            if (root.TryGetProperty("display_name", out var displayName))
                metadata.DisplayName = displayName.GetString();

            if (root.TryGetProperty("displayName", out var displayName2))
                metadata.DisplayName ??= displayName2.GetString();

            if (root.TryGetProperty("username", out var username))
                metadata.Username = username.GetString();

            if (root.TryGetProperty("about", out var about))
                metadata.About = about.GetString();

            if (root.TryGetProperty("picture", out var picture))
                metadata.Picture = picture.GetString();

            if (root.TryGetProperty("banner", out var banner))
                metadata.Banner = banner.GetString();

            if (root.TryGetProperty("website", out var website))
                metadata.Website = website.GetString();

            if (root.TryGetProperty("nip05", out var nip05))
                metadata.Nip05 = nip05.GetString();

            if (root.TryGetProperty("lud16", out var lud16))
                metadata.Lud16 = lud16.GetString();

            if (root.TryGetProperty("lud06", out var lud06))
                metadata.Lud06 = lud06.GetString();

            _logger.LogDebug("Parsed metadata - Name: {Name}, DisplayName: {DisplayName}, Nip05: {Nip05}",
                metadata.Name, metadata.DisplayName, metadata.Nip05);

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse metadata content: {Content}", content[..Math.Min(100, content.Length)]);
            return null;
        }
    }

    public string? NpubToHex(string npub)
    {
        if (string.IsNullOrEmpty(npub))
            return null;

        try
        {
            if (npub.StartsWith("npub"))
            {
                var bytes = Bech32.Decode(npub, out var hrp);
                if (hrp == "npub" && bytes.Length == 32)
                {
                    return Convert.ToHexString(bytes).ToLowerInvariant();
                }
            }
            else if (npub.Length == 64)
            {
                // Already hex
                return npub.ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert npub to hex: {Npub}", npub);
        }

        return null;
    }

    public byte[] EncryptNip44(string plaintext, string senderPrivateKeyHex, string recipientPublicKeyHex)
    {
        // TODO: Implement NIP-44 encryption
        // For now, use a placeholder that will be replaced with Blockcore implementation
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        return plaintextBytes; // Placeholder - NOT SECURE
    }

    public string DecryptNip44(byte[] ciphertext, string recipientPrivateKeyHex, string senderPublicKeyHex)
    {
        // TODO: Implement NIP-44 decryption
        // For now, use a placeholder that will be replaced with Blockcore implementation
        return Encoding.UTF8.GetString(ciphertext); // Placeholder - NOT SECURE
    }

    private async Task<string> PublishEventAsync(int kind, string content, List<List<string>> tags, string privateKeyHex)
    {
        _logger.LogInformation("Publishing event kind {Kind} to {Count} relays", kind, _relayConnections.Count);

        // Derive public key from private key
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var publicKeyBytes = DerivePublicKey(privateKeyBytes);
        var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();

        // Create event
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Build the event for signing (NIP-01 format)
        // [0, pubkey, created_at, kind, tags, content]
        // Use Utf8JsonWriter for deterministic JSON output — boxing List<List<string>>
        // as object in object[] causes System.Text.Json to serialize tags incorrectly.
        var serializedForId = SerializeForEventId(publicKeyHex, createdAt, kind, tags, content);
        _logger.LogDebug("Event serialization for ID: {Serialized}", serializedForId);

        // Calculate event ID (SHA256 of serialized event)
        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // Sign the event ID with Schnorr signature (BIP-340)
        var signature = SignSchnorr(eventIdBytes, privateKeyBytes);
        var signatureHex = Convert.ToHexString(signature).ToLowerInvariant();

        // Build the full event JSON using Utf8JsonWriter for consistency
        var eventMessage = SerializeEventMessage(eventId, publicKeyHex, createdAt, kind, tags, content, signatureHex);
        var eventBytes = Encoding.UTF8.GetBytes(eventMessage);

        _logger.LogDebug("Publishing event {EventId} to relays", eventId);

        // Send to all connected relays
        var publishTasks = new List<Task>();
        foreach (var (relayUrl, ws) in _relayConnections)
        {
            if (ws.State == WebSocketState.Open)
            {
                publishTasks.Add(SendToRelayAsync(relayUrl, ws, eventBytes));
            }
        }

        await Task.WhenAll(publishTasks);

        _logger.LogInformation("Event {EventId} published to {Count} relays", eventId, publishTasks.Count);
        return eventId;
    }

    private async Task SendToRelayAsync(string relayUrl, ClientWebSocket ws, byte[] data)
    {
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogDebug("Sent event to relay {RelayUrl}", relayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send event to relay {RelayUrl}", relayUrl);
        }
    }

    /// <summary>
    /// Serialize the pre-event array for NIP-01 event ID computation.
    /// Uses Utf8JsonWriter for deterministic output instead of JsonSerializer.Serialize(object[])
    /// which incorrectly serializes boxed List&lt;List&lt;string&gt;&gt; values.
    /// </summary>
    private static string SerializeForEventId(string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(0);
            writer.WriteStringValue(pubkey);
            writer.WriteNumberValue(createdAt);
            writer.WriteNumberValue(kind);
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
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serialize the full EVENT message for sending to relays.
    /// </summary>
    private static string SerializeEventMessage(string eventId, string pubkey, long createdAt, int kind, List<List<string>> tags, string content, string sig)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("EVENT");
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", pubkey);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
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
            writer.WriteString("content", content);
            writer.WriteString("sig", sig);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] SignSchnorr(byte[] message, byte[] privateKey)
    {
        if (!Context.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey) || ecPrivKey is null)
        {
            throw new ArgumentException("Invalid private key for signing");
        }

        // Use BIP-340 Schnorr signature
        var sig = ecPrivKey.SignBIP340(message);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        return sigBytes;
    }

    private static byte[] DerivePublicKey(byte[] privateKey)
    {
        // Use secp256k1 to derive the public key (x-only for Nostr)
        if (!Context.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey) || ecPrivKey is null)
        {
            throw new ArgumentException("Invalid private key");
        }

        var pubKey = ecPrivKey.CreateXOnlyPubKey();
        var publicKeyBytes = new byte[32];
        pubKey.WriteToSpan(publicKeyBytes);
        return publicKeyBytes;
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();

        // Dispose relay connections
        foreach (var (_, cts) in _relayListeners)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _relayListeners.Clear();

        foreach (var (_, ws) in _relayConnections)
        {
            ws.Dispose();
        }
        _relayConnections.Clear();

        _connectionStatus.Dispose();
        _events.Dispose();
        _welcomeMessages.Dispose();
        _groupMessages.Dispose();

        _disposed = true;
    }
}
