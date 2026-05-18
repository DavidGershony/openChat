using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Scramble.Presentation.Services;

namespace Scramble.UI.Services;

public class AvaloniaClipboard : IPlatformClipboard
{
    /// <summary>
    /// Resolves the platform clipboard by finding the TopLevel from the current
    /// ApplicationLifetime. Supports desktop (IClassicDesktopStyleApplicationLifetime)
    /// and mobile/single-view (ISingleViewApplicationLifetime) hosts.
    ///
    /// Uses TopLevel.GetTopLevel() — the recommended Avalonia 12 pattern — instead of
    /// accessing Window.Clipboard directly, which may return null depending on the
    /// platform backend initialisation state.
    /// </summary>
    private static IClipboard? GetClipboard()
    {
        var lifetime = Application.Current?.ApplicationLifetime;

        if (lifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            return TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;

        if (lifetime is ISingleViewApplicationLifetime singleView && singleView.MainView != null)
            return TopLevel.GetTopLevel(singleView.MainView)?.Clipboard;

        return null;
    }

    public async Task SetTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard != null)
            return await clipboard.TryGetTextAsync();
        return null;
    }
}
