using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

// BridgeAuthService is in OpenChat.Core.Services

namespace OpenChat.Android.Services.Bridge;

/// <summary>
/// Routes incoming HTTP requests to handler methods and serializes JSON responses.
/// Provides the REST API that thin clients (Fitbit companion, future Wear OS) consume.
/// </summary>
public class BridgeRequestRouter
{
    private readonly ILogger<BridgeRequestRouter> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly BridgeAuthService _authService;
    private readonly BridgeSseHandler _sseHandler;
    private string? _currentUserNpub;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // Route patterns
    private static readonly Regex ChatMessagesPattern = new(@"^/api/v1/chats/([^/]+)/messages$", RegexOptions.Compiled);
    private static readonly Regex ChatReadPattern = new(@"^/api/v1/chats/([^/]+)/read$", RegexOptions.Compiled);

    public BridgeRequestRouter(
        IMessageService messageService,
        IStorageService storageService,
        BridgeAuthService authService,
        BridgeSseHandler sseHandler)
    {
        _logger = LoggingConfiguration.CreateLogger<BridgeRequestRouter>();
        _messageService = messageService;
        _storageService = storageService;
        _authService = authService;
        _sseHandler = sseHandler;
    }

    /// <summary>
    /// Set the current user's npub for status responses.
    /// </summary>
    public void SetCurrentUserNpub(string? npub)
    {
        _currentUserNpub = npub;
    }

    /// <summary>
    /// Route an incoming request and write the response.
    /// </summary>
    public async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        _logger.LogDebug("Bridge request: {Method} {Path}", method, path);

        try
        {
            // Auth pairing endpoint — no token required
            if (path == "/api/v1/auth/pair" && method == "POST")
            {
                await HandlePairAsync(request, response);
                return;
            }

            // SSE events endpoint — requires auth, handled separately
            if (path == "/api/v1/events" && method == "GET")
            {
                if (!Authenticate(request, response)) return;
                await _sseHandler.HandleSseConnectionAsync(response);
                return;
            }

            // All other endpoints require auth
            if (!Authenticate(request, response)) return;

            // Route to handlers
            if (path == "/api/v1/auth/status" && method == "GET")
            {
                await WriteJsonAsync(response, 200, new { authenticated = true, user_npub = _currentUserNpub });
                return;
            }

            if (path == "/api/v1/chats" && method == "GET")
            {
                await HandleGetChatsAsync(response);
                return;
            }

            var messagesMatch = ChatMessagesPattern.Match(path);
            if (messagesMatch.Success)
            {
                var chatId = Uri.UnescapeDataString(messagesMatch.Groups[1].Value);
                if (method == "GET")
                {
                    var limitStr = request.QueryString["limit"];
                    var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 100) : 20;
                    var offsetStr = request.QueryString["offset"];
                    var offset = int.TryParse(offsetStr, out var o) ? Math.Max(o, 0) : 0;
                    await HandleGetMessagesAsync(response, chatId, limit, offset);
                    return;
                }
                if (method == "POST")
                {
                    await HandleSendMessageAsync(request, response, chatId);
                    return;
                }
            }

            var readMatch = ChatReadPattern.Match(path);
            if (readMatch.Success && method == "POST")
            {
                var chatId = Uri.UnescapeDataString(readMatch.Groups[1].Value);
                await HandleMarkReadAsync(response, chatId);
                return;
            }

            // Not found
            await WriteJsonAsync(response, 404, new { error = "not_found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge request failed: {Method} {Path}", method, path);
            try
            {
                await WriteJsonAsync(response, 500, new { error = "internal_error" });
            }
            catch
            {
                // Response may already be closed
            }
        }
    }

    private bool Authenticate(HttpListenerRequest request, HttpListenerResponse response)
    {
        var authHeader = request.Headers["Authorization"];
        var token = BridgeAuthService.ExtractBearerToken(authHeader);

        if (token == null || !_authService.ValidateToken(token))
        {
            _logger.LogWarning("Unauthorized bridge request: {Method} {Path}", request.HttpMethod, request.Url?.AbsolutePath);
            WriteJsonAsync(response, 401, new { error = "unauthorized" }).GetAwaiter().GetResult();
            return false;
        }

        return true;
    }

    private async Task HandlePairAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBodyAsync(request);
        if (body == null)
        {
            await WriteJsonAsync(response, 400, new { error = "invalid_body" });
            return;
        }

        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            code = doc.RootElement.GetProperty("code").GetString();
        }
        catch
        {
            await WriteJsonAsync(response, 400, new { error = "invalid_json" });
            return;
        }

        if (string.IsNullOrEmpty(code))
        {
            await WriteJsonAsync(response, 400, new { error = "missing_code" });
            return;
        }

        var token = await _authService.TryPairAsync(code);
        if (token == null)
        {
            await WriteJsonAsync(response, 403, new { error = "invalid_or_expired_code" });
            return;
        }

        await WriteJsonAsync(response, 200, new { token });
    }

    private async Task HandleGetChatsAsync(HttpListenerResponse response)
    {
        var chats = await _messageService.GetChatsAsync();
        var currentUser = await _storageService.GetCurrentUserAsync();
        var currentUserPubKey = currentUser?.PublicKeyHex ?? "";

        var chatDtos = new List<object>();
        foreach (var chat in chats.Where(c => !c.IsArchived).OrderByDescending(c => c.LastActivityAt))
        {
            string? lastMessageContent = null;
            string? lastMessageSender = null;
            string? lastMessageTimestamp = null;

            if (chat.LastMessage != null)
            {
                lastMessageContent = FormatMessageContent(chat.LastMessage);
                lastMessageSender = await GetSenderDisplayNameAsync(chat.LastMessage.SenderPublicKey, currentUserPubKey);
                lastMessageTimestamp = chat.LastMessage.Timestamp.ToString("o");
            }

            chatDtos.Add(new
            {
                id = chat.Id,
                name = chat.Name,
                type = chat.Type.ToString().ToLowerInvariant(),
                unread_count = chat.UnreadCount,
                last_message = chat.LastMessage != null ? new
                {
                    content = lastMessageContent,
                    sender_name = lastMessageSender,
                    timestamp = lastMessageTimestamp
                } : null,
                last_activity_at = chat.LastActivityAt.ToString("o")
            });
        }

        await WriteJsonAsync(response, 200, new { chats = chatDtos });
    }

    private async Task HandleGetMessagesAsync(HttpListenerResponse response, string chatId, int limit, int offset)
    {
        var messages = await _messageService.GetMessagesAsync(chatId, limit, offset);
        var currentUser = await _storageService.GetCurrentUserAsync();
        var currentUserPubKey = currentUser?.PublicKeyHex ?? "";

        var messageDtos = new List<object>();
        foreach (var msg in messages)
        {
            messageDtos.Add(new
            {
                id = msg.Id,
                sender_name = await GetSenderDisplayNameAsync(msg.SenderPublicKey, currentUserPubKey),
                sender_pubkey = msg.SenderPublicKey.Length > 12 ? msg.SenderPublicKey[..12] + "..." : msg.SenderPublicKey,
                content = FormatMessageContent(msg),
                timestamp = msg.Timestamp.ToString("o"),
                is_from_current_user = msg.IsFromCurrentUser,
                type = msg.Type.ToString().ToLowerInvariant()
            });
        }

        await WriteJsonAsync(response, 200, new { messages = messageDtos });
    }

    private async Task HandleSendMessageAsync(HttpListenerRequest request, HttpListenerResponse response, string chatId)
    {
        var body = await ReadBodyAsync(request);
        if (body == null)
        {
            await WriteJsonAsync(response, 400, new { error = "invalid_body" });
            return;
        }

        string? content = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            content = doc.RootElement.GetProperty("content").GetString();
        }
        catch
        {
            await WriteJsonAsync(response, 400, new { error = "invalid_json" });
            return;
        }

        if (string.IsNullOrEmpty(content))
        {
            await WriteJsonAsync(response, 400, new { error = "missing_content" });
            return;
        }

        var message = await _messageService.SendMessageAsync(chatId, content);
        _logger.LogInformation("Bridge sent message to chat");

        await WriteJsonAsync(response, 200, new { message_id = message.Id, status = "sent" });
    }

    private async Task HandleMarkReadAsync(HttpListenerResponse response, string chatId)
    {
        await _messageService.MarkAsReadAsync(chatId);
        await WriteJsonAsync(response, 200, new { ok = true });
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

    private async Task<string> GetSenderDisplayNameAsync(string publicKeyHex, string currentUserPubKey)
    {
        if (string.Equals(publicKeyHex, currentUserPubKey, StringComparison.OrdinalIgnoreCase))
            return "You";

        var user = await _storageService.GetUserByPublicKeyAsync(publicKeyHex);
        if (user != null)
        {
            if (!string.IsNullOrEmpty(user.DisplayName)) return user.DisplayName;
            if (!string.IsNullOrEmpty(user.Username)) return user.Username;
        }

        return publicKeyHex.Length > 8 ? publicKeyHex[..8] + "..." : publicKeyHex;
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return null;
        using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object data)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        response.ContentLength64 = json.Length;
        await response.OutputStream.WriteAsync(json);
        response.Close();
    }
}
