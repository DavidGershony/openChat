using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Encapsulates a single WebSocket connection to a Nostr relay with thread-safe sends,
/// auto-reconnect, subscription tracking, and .NET 9 KeepAlive dead-connection detection.
/// </summary>
public class NostrRelayConnection : IAsyncDisposable
{
    private readonly ILogger<NostrRelayConnection> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _activeSubscriptions = new(); // subId -> filterJson
    private readonly Subject<string> _messages = new();
    private readonly Subject<NostrConnectionStatus> _connectionStatus = new();
    private readonly bool _autoReconnect;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private int _backoffSeconds;
    private volatile bool _isConnected;
    private volatile bool _disposed;

    private const int MaxBackoffSeconds = 60;
    private const int SendTimeoutMs = 15_000;
    private const int ConnectTimeoutMs = 10_000;
    private const int ListenerBufferSize = 65536;

    public string RelayUrl { get; }
    public bool IsConnected => _isConnected;
    public IObservable<string> Messages => _messages.AsObservable();
    public IObservable<NostrConnectionStatus> ConnectionStatus => _connectionStatus.AsObservable();
    public IReadOnlyDictionary<string, string> ActiveSubscriptions => _activeSubscriptions;

    /// <summary>
    /// Creates a persistent relay connection with auto-reconnect enabled.
    /// </summary>
    public NostrRelayConnection(string relayUrl, ILogger<NostrRelayConnection> logger)
        : this(relayUrl, logger, autoReconnect: true) { }

    private NostrRelayConnection(string relayUrl, ILogger<NostrRelayConnection> logger, bool autoReconnect)
    {
        RelayUrl = relayUrl ?? throw new ArgumentNullException(nameof(relayUrl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _autoReconnect = autoReconnect;
    }

    /// <summary>
    /// Creates a temporary relay connection (no auto-reconnect, caller-controlled lifetime).
    /// </summary>
    public static NostrRelayConnection CreateTemporary(string relayUrl, ILogger<NostrRelayConnection> logger)
    {
        return new NostrRelayConnection(relayUrl, logger, autoReconnect: false);
    }

    /// <summary>
    /// Connects to the relay and starts the listener loop.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NostrRelayConnection));

        _logger.LogDebug("Connecting to relay: {RelayUrl}", RelayUrl);

        var ws = new ClientWebSocket();

        // .NET 9 KeepAlive: detect dead connections automatically
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeoutMs);

        await ws.ConnectAsync(new Uri(RelayUrl), connectCts.Token);

        _ws = ws;
        _isConnected = true;
        _backoffSeconds = 0; // Reset backoff on successful connect

        _connectionStatus.OnNext(new NostrConnectionStatus
        {
            RelayUrl = RelayUrl,
            IsConnected = true
        });

        _logger.LogInformation("Connected to relay: {RelayUrl}", RelayUrl);

        // Start listener
        _listenerCts = new CancellationTokenSource();
        _listenerTask = RunListenerAsync(_listenerCts.Token);
    }

    /// <summary>
    /// Sends data to the relay with thread-safe serialization and timeout.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NostrRelayConnection));

        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send to {RelayUrl} — not connected (state: {State})",
                RelayUrl, ws?.State.ToString() ?? "null");
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SendTimeoutMs);

        if (!await _sendLock.WaitAsync(SendTimeoutMs, timeoutCts.Token))
        {
            _logger.LogWarning("Send lock timeout on {RelayUrl}", RelayUrl);
            return;
        }

        try
        {
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, timeoutCts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Registers a subscription and sends the REQ message to the relay.
    /// The subscription is tracked for auto-replay on reconnect.
    /// </summary>
    public async Task RegisterSubscriptionAsync(string subId, string filterJson)
    {
        _activeSubscriptions[subId] = filterJson;

        var reqMessage = $"[\"REQ\",\"{subId}\",{filterJson}]";
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);

        try
        {
            await SendAsync(reqBytes);
            _logger.LogDebug("Registered subscription {SubId} on {RelayUrl}", subId, RelayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send REQ for subscription {SubId} on {RelayUrl}", subId, RelayUrl);
        }
    }

    /// <summary>
    /// Unregisters a subscription, removes tracking, and sends CLOSE to the relay.
    /// </summary>
    public async Task UnregisterSubscriptionAsync(string subId)
    {
        if (_activeSubscriptions.TryRemove(subId, out _))
        {
            var closeMessage = $"[\"CLOSE\",\"{subId}\"]";
            var closeBytes = Encoding.UTF8.GetBytes(closeMessage);

            try
            {
                await SendAsync(closeBytes);
                _logger.LogDebug("Unregistered subscription {SubId} on {RelayUrl}", subId, RelayUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send CLOSE for subscription {SubId} on {RelayUrl}", subId, RelayUrl);
            }
        }
    }

    /// <summary>
    /// Returns true if a subscription with the given ID is tracked (for H2 validation).
    /// </summary>
    public bool HasSubscription(string subId) => _activeSubscriptions.ContainsKey(subId);

    /// <summary>
    /// Gracefully disconnects from the relay without auto-reconnect.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _isConnected = false;

        // Cancel listener first
        if (_listenerCts != null)
        {
            await _listenerCts.CancelAsync();
        }

        // Wait for listener to finish
        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Listener task did not complete within timeout for {RelayUrl}", RelayUrl);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for listener task on {RelayUrl}", RelayUrl);
            }
        }

        // Close WebSocket gracefully
        var ws = _ws;
        _ws = null;
        if (ws != null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket for {RelayUrl}", RelayUrl);
            }
            finally
            {
                ws.Dispose();
            }
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;

        _connectionStatus.OnNext(new NostrConnectionStatus
        {
            RelayUrl = RelayUrl,
            IsConnected = false
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();

        _activeSubscriptions.Clear();
        _sendLock.Dispose();
        _messages.Dispose();
        _connectionStatus.Dispose();
    }

    // --- Private implementation ---

    private async Task RunListenerAsync(CancellationToken ct)
    {
        var buffer = new byte[ListenerBufferSize];
        var wasCancelled = false;

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveFullMessageAsync(_ws, buffer, ct);
                if (msg == null || msg.Value.Type == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Relay {RelayUrl} closed connection", RelayUrl);
                    break;
                }

                _messages.OnNext(msg.Value.Text);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Listener cancelled for {RelayUrl}", RelayUrl);
            wasCancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listening to relay {RelayUrl}", RelayUrl);
        }
        finally
        {
            _isConnected = false;
            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = RelayUrl,
                IsConnected = false
            });
        }

        // Auto-reconnect with exponential backoff (unless deliberately cancelled or disposed)
        if (_autoReconnect && !wasCancelled && !_disposed)
        {
            await ReconnectWithBackoffAsync();
        }
    }

    private async Task ReconnectWithBackoffAsync()
    {
        var backoff = _backoffSeconds <= 0 ? 1 : Math.Min(_backoffSeconds * 2, MaxBackoffSeconds);
        _backoffSeconds = backoff;

        _logger.LogInformation("Auto-reconnecting to {RelayUrl} in {Seconds}s", RelayUrl, backoff);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(backoff));
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (_disposed) return;

        // Dispose old WebSocket
        var oldWs = _ws;
        _ws = null;
        if (oldWs != null)
        {
            try { oldWs.Dispose(); } catch { }
        }

        _listenerCts?.Dispose();
        _listenerCts = null;

        try
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

            using var connectCts = new CancellationTokenSource(ConnectTimeoutMs);
            await ws.ConnectAsync(new Uri(RelayUrl), connectCts.Token);

            _ws = ws;
            _isConnected = true;
            _backoffSeconds = 0; // Reset on success

            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = RelayUrl,
                IsConnected = true
            });

            _logger.LogInformation("Reconnected to relay: {RelayUrl}", RelayUrl);

            // Replay all tracked subscriptions
            await ReplaySubscriptionsAsync();

            // Restart listener
            _listenerCts = new CancellationTokenSource();
            _listenerTask = RunListenerAsync(_listenerCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to {RelayUrl}", RelayUrl);

            _connectionStatus.OnNext(new NostrConnectionStatus
            {
                RelayUrl = RelayUrl,
                IsConnected = false,
                Error = ex.Message
            });

            // Retry: recurse with increased backoff
            if (!_disposed)
            {
                await ReconnectWithBackoffAsync();
            }
        }
    }

    private async Task ReplaySubscriptionsAsync()
    {
        foreach (var (subId, filterJson) in _activeSubscriptions)
        {
            var reqMessage = $"[\"REQ\",\"{subId}\",{filterJson}]";
            var reqBytes = Encoding.UTF8.GetBytes(reqMessage);

            try
            {
                await SendAsync(reqBytes);
                _logger.LogDebug("Replayed subscription {SubId} on {RelayUrl}", subId, RelayUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to replay subscription {SubId} on {RelayUrl}", subId, RelayUrl);
            }
        }

        if (_activeSubscriptions.Count > 0)
        {
            _logger.LogInformation("Replayed {Count} subscriptions on {RelayUrl}", _activeSubscriptions.Count, RelayUrl);
        }
    }

    /// <summary>
    /// Maximum WebSocket message size (16 MB). Messages exceeding this are dropped to prevent DoS.
    /// </summary>
    private const int MaxWebSocketMessageSize = 16 * 1024 * 1024;

    private static async Task<(WebSocketMessageType Type, string Text)?> ReceiveFullMessageAsync(
        ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, string.Empty);
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxWebSocketMessageSize)
                return null; // message too large, drop it
        } while (!result.EndOfMessage);

        return (result.MessageType, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}
