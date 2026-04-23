namespace OpenChat.Presentation.Services;

public interface INotificationService
{
    /// <summary>
    /// Show a native OS notification for an incoming chat message.
    /// </summary>
    void ShowMessageNotification(string chatId, string chatName, string senderName, string messagePreview);

    /// <summary>
    /// Clear notifications for a specific chat (e.g., when the user opens that chat).
    /// </summary>
    void ClearNotificationsForChat(string chatId);
}
