using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Services;

namespace OpenChat.Presentation.Services;

public class NotificationOrchestrator : IDisposable
{
    private readonly IMessageService _messageService;
    private readonly ILogger<NotificationOrchestrator> _logger;
    private IDisposable? _subscription;

    /// <summary>Platform-specific notification implementation. Set at app startup.</summary>
    public static INotificationService? NotificationService { get; set; }

    /// <summary>Chat ID currently visible to the user. Set by ChatViewModel.</summary>
    public static string? ActiveChatId { get; set; }

    /// <summary>Whether the app window/activity is in the foreground. Set by platform lifecycle.</summary>
    public static bool IsAppInForeground { get; set; } = true;

    public NotificationOrchestrator(IMessageService messageService)
    {
        _messageService = messageService;
        _logger = LoggingConfiguration.CreateLogger<NotificationOrchestrator>();
    }

    public void Initialize()
    {
        _subscription = _messageService.NewMessages
            .Where(m => !m.IsFromCurrentUser)
            .Subscribe(OnNewMessage);

        _logger.LogInformation("NotificationOrchestrator initialized");
    }

    private async void OnNewMessage(OpenChat.Core.Models.Message message)
    {
        try
        {
            if (NotificationService == null) return;

            var chat = await _messageService.GetChatAsync(message.ChatId);
            if (chat == null) return;
            if (chat.IsMuted) return;

            if (ActiveChatId == message.ChatId && IsAppInForeground)
                return;

            var senderName = message.Sender?.DisplayName
                             ?? message.Sender?.Username
                             ?? message.SenderPublicKey[..12] + "...";

            var preview = message.Type switch
            {
                OpenChat.Core.Models.MessageType.Image => "Sent an image",
                OpenChat.Core.Models.MessageType.Audio => "Sent a voice message",
                OpenChat.Core.Models.MessageType.File => "Sent a file",
                _ => message.Content.Length > 200 ? message.Content[..200] + "..." : message.Content
            };

            NotificationService.ShowMessageNotification(message.ChatId, chat.Name, senderName, preview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show notification for message {MessageId}", message.Id);
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
