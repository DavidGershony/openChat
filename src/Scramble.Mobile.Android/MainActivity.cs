using System.Collections.Concurrent;
using Android.App;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia.Android;

namespace Scramble.MobileAndroid;

[Activity(
    Label = "Scramble",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// Singleton so App.axaml.cs can wire the permission request delegate.
    /// Set in OnCreate, cleared in OnDestroy.
    /// </summary>
    public static MainActivity? Current { get; private set; }

    private int _nextRequestCode = 2000;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingPermissions = new();

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Request runtime permissions and return true if all were granted.
    /// Skips the prompt for permissions that are already granted.
    /// </summary>
    public Task<bool> RequestPermissionsAsync(string[] permissions)
    {
        // Filter to only permissions that haven't been granted yet
        var needed = permissions
            .Where(p => ContextCompat.CheckSelfPermission(this, p) != Permission.Granted)
            .ToArray();

        if (needed.Length == 0)
            return Task.FromResult(true);

        var requestCode = Interlocked.Increment(ref _nextRequestCode);
        var tcs = new TaskCompletionSource<bool>();
        _pendingPermissions[requestCode] = tcs;

        ActivityCompat.RequestPermissions(this, needed, requestCode);

        return tcs.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (_pendingPermissions.TryRemove(requestCode, out var tcs))
        {
            var allGranted = grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted);
            tcs.TrySetResult(allGranted);
        }
    }
}
