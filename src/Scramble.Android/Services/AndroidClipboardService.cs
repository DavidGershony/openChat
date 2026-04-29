using Android.Content;
using Scramble.Presentation.Services;

namespace Scramble.Android.Services;

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
            var clip = ClipData.NewPlainText("Scramble", text);
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
