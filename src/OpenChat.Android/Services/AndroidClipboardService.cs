using Android.Content;
using OpenChat.Presentation.Services;

namespace OpenChat.Android.Services;

public class AndroidClipboardService : IPlatformClipboard
{
    private readonly Context _context;

    public AndroidClipboardService(Context context)
    {
        _context = context;
    }

    public Task SetTextAsync(string text)
    {
        var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
        if (clipboard != null)
        {
            var clip = ClipData.NewPlainText("OpenChat", text);
            clipboard.PrimaryClip = clip;
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync()
    {
        var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
        if (clipboard?.HasPrimaryClip == true)
        {
            var item = clipboard.PrimaryClip?.GetItemAt(0);
            return Task.FromResult(item?.Text);
        }
        return Task.FromResult<string?>(null);
    }
}
