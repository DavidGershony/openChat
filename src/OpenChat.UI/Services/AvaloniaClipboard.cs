using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using OpenChat.Presentation.Services;

namespace OpenChat.UI.Services;

public class AvaloniaClipboard : IPlatformClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
    }

    public async Task<string?> GetTextAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard != null)
            {
                return await clipboard.GetTextAsync();
            }
        }
        return null;
    }
}
