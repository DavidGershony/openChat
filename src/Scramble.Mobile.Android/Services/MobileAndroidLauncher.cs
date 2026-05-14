using Android.Content;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.UI.Services;

namespace Scramble.MobileAndroid.Services;

/// <summary>
/// Android-specific launcher that extends <see cref="AvaloniaLauncher"/> with
/// native share intent support. Uses <see cref="AndroidX.Core.Content.FileProvider"/>
/// to securely expose files to other apps via content:// URIs.
/// </summary>
public class MobileAndroidLauncher : AvaloniaLauncher
{
    private static readonly ILogger<MobileAndroidLauncher> _logger =
        LoggingConfiguration.CreateLogger<MobileAndroidLauncher>();

    public override bool IsMobile => true;

    /// <summary>
    /// Shares a file using Android's native share intent (ACTION_SEND).
    /// The file is exposed via FileProvider so the receiving app gets a
    /// content:// URI with temporary read permission.
    /// </summary>
    public override void ShareFile(string filePath, string mimeType, string title)
    {
        try
        {
            var context = Android.App.Application.Context;
            if (context == null)
            {
                _logger.LogWarning("Cannot share file: no Android context available");
                return;
            }

            var file = new Java.IO.File(filePath);
            if (!file.Exists())
            {
                _logger.LogWarning("Cannot share file: file does not exist at {Path}", filePath);
                return;
            }

            var authority = context.PackageName + ".fileprovider";
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, file);

            var intent = new Intent(Intent.ActionSend);
            intent.SetType(mimeType);
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.PutExtra(Intent.ExtraSubject, title);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            // NewTask is required when starting an activity from a non-Activity context
            intent.AddFlags(ActivityFlags.NewTask);

            var chooser = Intent.CreateChooser(intent, title);
            chooser!.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(chooser);

            _logger.LogInformation("Launched share intent for {FileName}", file.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to share file {Path}", filePath);
        }
    }
}
