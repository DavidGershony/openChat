using Android.Content;
using OpenChat.Presentation.Services;

namespace OpenChat.Android.Services;

public class AndroidLauncher : IPlatformLauncher
{
    private readonly Context _context;

    public AndroidLauncher(Context context)
    {
        _context = context;
    }

    public void OpenUrl(string url)
    {
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }

    public void OpenFolder(string path)
    {
        // Android doesn't have a direct "open folder" concept
        // Open the file manager at the path if possible
        var intent = new Intent(Intent.ActionView);
        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }
}
