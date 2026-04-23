using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Presentation.Services;

namespace OpenChat.UI.Services;

public class DesktopNotificationService : INotificationService
{
    private readonly ILogger<DesktopNotificationService> _logger = LoggingConfiguration.CreateLogger<DesktopNotificationService>();

    public void ShowMessageNotification(string chatId, string chatName, string senderName, string messagePreview)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowWindowsToast(chatName, senderName, messagePreview);
            else if (OperatingSystem.IsLinux())
                ShowLinuxNotification(chatName, senderName, messagePreview);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show desktop notification");
        }
    }

    public void ClearNotificationsForChat(string chatId)
    {
        // Desktop toast notifications auto-dismiss; clearing is best-effort.
    }

    private void ShowWindowsToast(string chatName, string senderName, string messagePreview)
    {
        var title = XmlEscape(chatName);
        var body = XmlEscape(chatName == senderName ? messagePreview : $"{senderName}: {messagePreview}");

        // Build toast XML directly — avoids all PowerShell string-escaping issues.
        var toastXml =
            "<toast>" +
            "<visual><binding template=\"ToastText02\">" +
            $"<text id=\"1\">{title}</text>" +
            $"<text id=\"2\">{body}</text>" +
            "</binding></visual>" +
            "</toast>";

        // Use -EncodedCommand (Base64 UTF-16LE) so message content can contain any characters.
        var script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null\n" +
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            $"$xml.LoadXml('{toastXml.Replace("'", "''")}')\n" +
            "$toast = New-Object Windows.UI.Notifications.ToastNotification -ArgumentList $xml\n" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('OpenChat').Show($toast)";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private void ShowLinuxNotification(string chatName, string senderName, string messagePreview)
    {
        var body = chatName == senderName ? messagePreview : $"{senderName}: {messagePreview}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { chatName, body, "--app-name=OpenChat" },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static string XmlEscape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("\n", " ")
            .Replace("\r", "");
    }
}
