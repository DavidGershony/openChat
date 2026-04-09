using System.Net;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.Android.Services.Bridge;

/// <summary>
/// Handles Server-Sent Events (SSE) connections for real-time push notifications
/// to thin clients. Subscribes to IMessageService observables and streams events.
/// </summary>
public class BridgeSseHandler : IDisposable
{
    private readonly ILogger<BridgeSseHandler> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly List<SseClient> _clients = new();
    private readonly object _clientsLock = new();
    private IDisposable? _newMessagesSubscription;
    private IDisposable? _chatUpdatesSubscription;
    private string _currentUserPubKey = "";
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public BridgeSseHandler(IMessageService messageService, IStorageService storageService)
    {
        _logger = LoggingConfiguration.CreateLogger<BridgeSseHandler>();
        _messageService = messageService;
        _storageService = storageService;
    }

    /// <summary>
    /// Start subscribing to message service observables.
    /// </summary>
    public async Task StartAsync()
    {
        var currentUser = await _storageService.GetCurrentUserAsync();
        _currentUserPubKey = currentUser?.PublicKeyHex ?? "";

        _newMessagesSubscription = _messageService.NewMessages
            .Subscribe(OnNewMessage);

        _chatUpdatesSubscription = _messageService.ChatUpdates
            .Subscribe(OnChatUpdate);

        _logger.LogInformation("SSE handler started, listening for events");
    }

    /// <summary>
    /// Handle an incoming SSE connection. Keeps the response open until the client disconnects.
    /// </summary>
    public async Task HandleSseConnectionAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        response.Headers.Add("Access-Control-Allow-Origin", "*");

        var client = new SseClient(response);

        lock (_clientsLock)
        {
            _clients.Add(client);
        }

        _logger.LogInformation("SSE client connected (total: {Count})", _clients.Count);

        // Send initial keepalive
        await client.SendCommentAsync("connected");

        // Keep connection open until client disconnects or cancellation
        try
        {
            // Send keepalive every 30 seconds
            while (!client.IsDisconnected)
            {
                await Task.Delay(30_000, client.CancellationToken);
                await client.SendCommentAsync("keepalive");
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SSE client disconnected: {Reason}", ex.Message);
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }
            client.Dispose();
            _logger.LogInformation("SSE client disconnected (remaining: {Count})", _clients.Count);
        }
    }

    private async void OnNewMessage(Message message)
    {
        try
        {
            var senderName = await GetSenderDisplayNameAsync(message.SenderPublicKey);
            var content = FormatMessageContent(message);

            var data = new
            {
                chat_id = message.ChatId,
                message_id = message.Id,
                sender_name = senderName,
                content,
                timestamp = message.Timestamp.ToString("o"),
                is_from_current_user = message.IsFromCurrentUser,
                type = message.Type.ToString().ToLowerInvariant()
            };

            await BroadcastEventAsync("new_message", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast new message SSE event");
        }
    }

    private async void OnChatUpdate(Chat chat)
    {
        try
        {
            string? lastContent = null;
            string? lastSender = null;
            string? lastTimestamp = null;

            if (chat.LastMessage != null)
            {
                lastContent = FormatMessageContent(chat.LastMessage);
                lastSender = await GetSenderDisplayNameAsync(chat.LastMessage.SenderPublicKey);
                lastTimestamp = chat.LastMessage.Timestamp.ToString("o");
            }

            var data = new
            {
                chat_id = chat.Id,
                name = chat.Name,
                unread_count = chat.UnreadCount,
                last_message = chat.LastMessage != null ? new
                {
                    content = lastContent,
                    sender_name = lastSender,
                    timestamp = lastTimestamp
                } : null
            };

            await BroadcastEventAsync("chat_update", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast chat update SSE event");
        }
    }

    private async Task BroadcastEventAsync(string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        SseClient[] snapshot;

        lock (_clientsLock)
        {
            snapshot = _clients.ToArray();
        }

        foreach (var client in snapshot)
        {
            try
            {
                await client.SendEventAsync(eventType, json);
            }
            catch
            {
                client.MarkDisconnected();
            }
        }
    }

    private async Task<string> GetSenderDisplayNameAsync(string publicKeyHex)
    {
        if (string.Equals(publicKeyHex, _currentUserPubKey, StringComparison.OrdinalIgnoreCase))
            return "You";

        var user = await _storageService.GetUserByPublicKeyAsync(publicKeyHex);
        if (user != null)
        {
            if (!string.IsNullOrEmpty(user.DisplayName)) return user.DisplayName;
            if (!string.IsNullOrEmpty(user.Username)) return user.Username;
        }

        return publicKeyHex.Length > 8 ? publicKeyHex[..8] + "..." : publicKeyHex;
    }

    private static string FormatMessageContent(Message message)
    {
        return message.Type switch
        {
            MessageType.Image => "[Photo]",
            MessageType.Audio => message.AudioDurationSeconds.HasValue
                ? $"[Voice note {message.AudioDurationSeconds.Value:F0}s]"
                : "[Voice note]",
            MessageType.File => $"[File: {message.FileName ?? "attachment"}]",
            MessageType.System => message.Content,
            _ => message.Content
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _newMessagesSubscription?.Dispose();
        _chatUpdatesSubscription?.Dispose();

        lock (_clientsLock)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }

        _logger.LogInformation("SSE handler disposed");
    }

    /// <summary>
    /// Represents a connected SSE client.
    /// </summary>
    private sealed class SseClient : IDisposable
    {
        private readonly HttpListenerResponse _response;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public bool IsDisconnected { get; private set; }
        public CancellationToken CancellationToken => _cts.Token;

        public SseClient(HttpListenerResponse response)
        {
            _response = response;
        }

        public async Task SendEventAsync(string eventType, string jsonData)
        {
            if (IsDisconnected) return;
            await _writeLock.WaitAsync();
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {jsonData}\n\n");
                await _response.OutputStream.WriteAsync(bytes);
                await _response.OutputStream.FlushAsync();
            }
            catch
            {
                MarkDisconnected();
                throw;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task SendCommentAsync(string comment)
        {
            if (IsDisconnected) return;
            await _writeLock.WaitAsync();
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes($": {comment}\n\n");
                await _response.OutputStream.WriteAsync(bytes);
                await _response.OutputStream.FlushAsync();
            }
            catch
            {
                MarkDisconnected();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void MarkDisconnected()
        {
            IsDisconnected = true;
            _cts.Cancel();
        }

        public void Dispose()
        {
            MarkDisconnected();
            _cts.Dispose();
            _writeLock.Dispose();
            try { _response.Close(); } catch { }
        }
    }
}
