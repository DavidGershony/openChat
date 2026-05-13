using Android.Content;
using Scramble.Presentation.Services;

namespace Scramble.Android.Services;

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

    public void ShareFile(string filePath, string mimeType, string title)
    {
        var file = new Java.IO.File(filePath);
        if (!file.Exists()) return;

        var authority = _context.PackageName + ".fileprovider";
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(_context, authority, file);

        var intent = new Intent(Intent.ActionSend);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraStream, uri);
        intent.PutExtra(Intent.ExtraSubject, title);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        intent.AddFlags(ActivityFlags.NewTask);

        _context.StartActivity(Intent.CreateChooser(intent, title));
    }
}
