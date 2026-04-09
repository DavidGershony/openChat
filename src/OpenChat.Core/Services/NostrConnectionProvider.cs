using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Centralized firewall between relay connections and NostrService.
/// Manages connections, maintains a unified subscription registry, and validates
/// every incoming event against its subscription filter before forwarding.
/// </summary>
public class NostrConnectionProvider : IAsyncDisposable
{
    private readonly ILogger<NostrConnectionProvider> _logger;
    private readonly ILogger<NostrRelayConnection> _connectionLogger;
    private readonly ConcurrentDictionary<string, NostrRelayConnection> _connections = new();
    private readonly ConcurrentDictionary<string, TrackedSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, IDisposable> _messageSubscriptions = new();
    private readonly Subject<(string relayUrl, string message)> _validatedMessages = new();
    private readonly Subject<NostrConnectionStatus> _connectionStatus = new();
    private volatile bool _disposed;

    /// <summary>
    /// Post-firewall message stream. Only events that match their subscription filter are emitted.
    /// </summary>
    public IObservable<(string relayUrl, string message)> ValidatedMessages => _validatedMessages.AsObservable();

    /// <summary>
    /// Aggregated connection status from all relay connections.
    /// </summary>
    public IObservable<NostrConnectionStatus> ConnectionStatus => _connectionStatus.AsObservable();

    public IReadOnlyList<string> ConnectedRelayUrls =>
        _connections.Where(kvp => kvp.Value.IsConnected).Select(kvp => kvp.Key).ToList();

    public NostrConnectionProvider(
        ILogger<NostrConnectionProvider> logger,
        ILogger<NostrRelayConnection> connectionLogger)
    {
        _logger = logger;
        _connectionLogger = connectionLogger;
    }

    public NostrConnectionProvider()
    {
        _logger = LoggingConfiguration.CreateLogger<NostrConnectionProvider>();
        _connectionLogger = LoggingConfiguration.CreateLogger<NostrRelayConnection>();
    }

    /// <summary>
    /// Gets or creates a connection for the given relay URL.
    /// </summary>
    public NostrRelayConnection GetOrCreateConnection(string relayUrl)
    {
        var normalized = relayUrl.TrimEnd('/');
        return _connections.GetOrAdd(normalized, url =>
        {
            var conn = new NostrRelayConnection(url, _connectionLogger);
            SubscribeToConnectionMessages(url, conn);
            return conn;
        });
    }

    /// <summary>
    /// Registers an externally-created connection with the provider.
    /// Used when NostrService creates connections with custom validation logic.
    /// </summary>
    public void RegisterConnection(string relayUrl, NostrRelayConnection connection)
    {
        var normalized = relayUrl.TrimEnd('/');
        _connections[normalized] = connection;
    }

    /// <summary>
    /// Removes a connection from the provider's registry without disposing it.
    /// The caller is responsible for disposal.
    /// </summary>
    public void UnregisterConnection(string relayUrl)
    {
        var normalized = relayUrl.TrimEnd('/');
        _connections.TryRemove(normalized, out _);

        // Remove this relay from all tracked subscriptions
        foreach (var tracked in _subscriptions.Values)
            tracked.RemoveRelay(normalized);
    }

    /// <summary>
    /// Gets an existing connection without creating one. Returns null if not found.
    /// </summary>
    public NostrRelayConnection? GetConnection(string relayUrl)
    {
        var normalized = relayUrl.TrimEnd('/');
        return _connections.TryGetValue(normalized, out var conn) ? conn : null;
    }

    /// <summary>
    /// Connects to a relay (creates the connection if needed).
    /// </summary>
    public async Task ConnectAsync(string relayUrl, CancellationToken ct = default)
    {
        var conn = GetOrCreateConnection(relayUrl);
        if (!conn.IsConnected)
        {
            await conn.ConnectAsync(ct);
        }
    }

    /// <summary>
    /// Disconnects a single relay and cleans up its subscriptions.
    /// </summary>
    public async Task DisconnectAsync(string relayUrl)
    {
        var normalized = relayUrl.TrimEnd('/');
        if (_connections.TryRemove(normalized, out var conn))
        {
            if (_messageSubscriptions.TryRemove(normalized, out var sub))
                sub.Dispose();

            // Remove this relay from all tracked subscriptions
            foreach (var tracked in _subscriptions.Values)
                tracked.RemoveRelay(normalized);

            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Disconnects all relays.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        var urls = _connections.Keys.ToList();
        foreach (var url in urls)
        {
            await DisconnectAsync(url);
        }
    }

    /// <summary>
    /// Registers a subscription with the provider and sends REQ to all specified relays.
    /// Returns the full subscription ID.
    /// </summary>
    public async Task<string> SubscribeAsync(string subIdPrefix, ParsedFilter filter, IEnumerable<string> relayUrls)
    {
        var subId = $"{subIdPrefix}_{Guid.NewGuid():N}"[..16];
        var tracked = new TrackedSubscription(subId, filter);
        _subscriptions[subId] = tracked;

        var filterJson = filter.ToFilterJson();

        foreach (var relayUrl in relayUrls)
        {
            var normalized = relayUrl.TrimEnd('/');
            tracked.AddRelay(normalized);

            if (_connections.TryGetValue(normalized, out var conn) && conn.IsConnected)
            {
                await SendReqAsync(conn, subId, filterJson);
            }
        }

        _logger.LogDebug("Registered subscription {SubId} with filter on {RelayCount} relays",
            subId, tracked.ActiveRelays.Count);

        return subId;
    }

    /// <summary>
    /// Registers a subscription for tracking/validation only (no REQ sent).
    /// Used when the caller manages REQ sending separately.
    /// </summary>
    public void TrackSubscription(string subId, ParsedFilter filter, IEnumerable<string> relayUrls)
    {
        var tracked = new TrackedSubscription(subId, filter);
        foreach (var relayUrl in relayUrls)
            tracked.AddRelay(relayUrl.TrimEnd('/'));
        _subscriptions[subId] = tracked;
    }

    /// <summary>
    /// Unsubscribes: sends CLOSE to relays and removes tracking.
    /// </summary>
    public async Task UnsubscribeAsync(string subId)
    {
        if (_subscriptions.TryRemove(subId, out var tracked))
        {
            var closeMessage = $"[\"CLOSE\",\"{subId}\"]";
            var closeBytes = Encoding.UTF8.GetBytes(closeMessage);

            foreach (var relayUrl in tracked.ActiveRelays)
            {
                if (_connections.TryGetValue(relayUrl, out var conn) && conn.IsConnected)
                {
                    try
                    {
                        await conn.SendAsync(closeBytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send CLOSE for {SubId} on {RelayUrl}", subId, relayUrl);
                    }
                }
            }

            _logger.LogDebug("Unsubscribed {SubId}", subId);
        }
    }

    /// <summary>
    /// Gets a tracked subscription by ID. Returns null if not found.
    /// </summary>
    public TrackedSubscription? GetSubscription(string subId) =>
        _subscriptions.TryGetValue(subId, out var tracked) ? tracked : null;

    /// <summary>
    /// Checks whether a subscription ID is tracked.
    /// </summary>
    public bool HasSubscription(string subId) => _subscriptions.ContainsKey(subId);

    /// <summary>
    /// The firewall: validates an event against its subscription filter.
    /// Returns true if the event passes, false if it should be dropped.
    /// </summary>
    public bool ValidateEvent(string subId, NostrEventReceived evt)
    {
        if (!_subscriptions.TryGetValue(subId, out var tracked))
        {
            _logger.LogWarning("H2: Dropping event — unknown subscription ID {SubId}", subId);
            return false;
        }

        if (!tracked.Filter.Matches(evt))
        {
            _logger.LogWarning(
                "H2: Dropping event kind {Kind} from {PubKey} — does not match filter for subscription {SubId}",
                evt.Kind, evt.PublicKey[..Math.Min(16, evt.PublicKey.Length)], subId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Replays all subscriptions for a given relay (called after reconnect).
    /// </summary>
    public async Task ReplaySubscriptionsForRelayAsync(string relayUrl)
    {
        var normalized = relayUrl.TrimEnd('/');
        if (!_connections.TryGetValue(normalized, out var conn))
            return;

        var count = 0;
        foreach (var tracked in _subscriptions.Values)
        {
            if (tracked.HasRelay(normalized))
            {
                await SendReqAsync(conn, tracked.SubId, tracked.Filter.ToFilterJson());
                count++;
            }
        }

        if (count > 0)
            _logger.LogInformation("Replayed {Count} subscriptions on {RelayUrl}", count, normalized);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _messageSubscriptions.Values)
            sub.Dispose();
        _messageSubscriptions.Clear();

        foreach (var conn in _connections.Values)
            await conn.DisposeAsync();
        _connections.Clear();

        _subscriptions.Clear();
        _validatedMessages.Dispose();
        _connectionStatus.Dispose();
    }

    // --- Private helpers ---

    private void SubscribeToConnectionMessages(string relayUrl, NostrRelayConnection conn)
    {
        var msgSub = conn.Messages.Subscribe(
            message => _validatedMessages.OnNext((relayUrl, message)),
            ex => _logger.LogError(ex, "Error on message stream from {RelayUrl}", relayUrl));

        _messageSubscriptions[relayUrl] = msgSub;

        // Forward connection status
        var statusSub = conn.ConnectionStatus.Subscribe(
            status => _connectionStatus.OnNext(status));

        // Combine both subscriptions for cleanup
        var existing = _messageSubscriptions.AddOrUpdate(
            relayUrl,
            msgSub,
            (_, old) => { old.Dispose(); return msgSub; });
    }

    private async Task SendReqAsync(NostrRelayConnection conn, string subId, string filterJson)
    {
        var reqMessage = $"[\"REQ\",\"{subId}\",{filterJson}]";
        var reqBytes = Encoding.UTF8.GetBytes(reqMessage);

        try
        {
            await conn.SendAsync(reqBytes);
            _logger.LogDebug("Sent REQ for {SubId} on {RelayUrl}", subId, conn.RelayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send REQ for {SubId} on {RelayUrl}", subId, conn.RelayUrl);
        }
    }
}
