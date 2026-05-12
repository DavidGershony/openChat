using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using Java.Util.Concurrent;
using Microsoft.Extensions.Logging;
using Scramble.Android.Services;
using Scramble.Core.Logging;

namespace Scramble.Android.Activities;

/// <summary>
/// Full-screen QR scanner. Hosts a CameraX <see cref="PreviewView"/> wired to
/// <see cref="MlKitQrCodeAnalyzer"/>; on the first decode returns the raw payload to
/// the caller via <see cref="ResultExtraScannedText"/> and finishes.
///
/// Always offers an "Enter manually" escape hatch so a permission denial or a
/// non-functional camera can never strand the user.
/// </summary>
[Activity(
    Label = "Scan QR",
    Theme = "@style/Theme.Material3.DayNight.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class ScanQrActivity : AppCompatActivity
{
    public const string ResultExtraScannedText = "scanned_text";
    private const int CameraPermissionRequest = 1001;

    private static readonly ILogger<ScanQrActivity> Logger = LoggingConfiguration.CreateLogger<ScanQrActivity>();

    private PreviewView? _previewView;
    private View? _permissionDeniedOverlay;
    private MlKitQrCodeAnalyzer? _analyzer;
    private IExecutorService? _analyzerExecutor;
    private ProcessCameraProvider? _cameraProvider;
    private bool _resultDelivered;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_scan_qr);

        _previewView = FindViewById<PreviewView>(Resource.Id.scan_qr_preview);
        _permissionDeniedOverlay = FindViewById<View>(Resource.Id.scan_qr_permission_denied);

        var toolbar = FindViewById<MaterialToolbar>(Resource.Id.scan_qr_toolbar)!;
        toolbar.NavigationClick += (_, _) => Finish();

        var manualButton = FindViewById<MaterialButton>(Resource.Id.scan_qr_manual_button)!;
        manualButton.Click += (_, _) => PromptManualEntry();

        var deniedManualButton = FindViewById<MaterialButton>(Resource.Id.scan_qr_denied_manual_button)!;
        deniedManualButton.Click += (_, _) => PromptManualEntry();

        var openSettingsButton = FindViewById<MaterialButton>(Resource.Id.scan_qr_open_settings_button)!;
        openSettingsButton.Click += (_, _) =>
        {
            var intent = new Intent(Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.FromParts("package", PackageName, null));
            StartActivity(intent);
        };

        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.Camera) == Permission.Granted)
        {
            StartCamera();
        }
        else
        {
            ActivityCompat.RequestPermissions(this, new[] { Manifest.Permission.Camera }, CameraPermissionRequest);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraPermissionRequest) return;

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartCamera();
        }
        else
        {
            _permissionDeniedOverlay!.Visibility = ViewStates.Visible;
        }
    }

    private void StartCamera()
    {
        _permissionDeniedOverlay!.Visibility = ViewStates.Gone;

        _analyzer = new MlKitQrCodeAnalyzer();
        _analyzer.QrCodeDetected += OnQrCodeDetected;
        _analyzerExecutor = Executors.NewSingleThreadExecutor()!;

        var providerFuture = ProcessCameraProvider.GetInstance(this);
        providerFuture.AddListener(new global::Java.Lang.Runnable(() =>
        {
            try
            {
                _cameraProvider = (ProcessCameraProvider)providerFuture.Get()!;
                BindUseCases();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to obtain CameraProvider");
                Toast.MakeText(this, "Camera unavailable", ToastLength.Long)?.Show();
                _permissionDeniedOverlay!.Visibility = ViewStates.Visible;
            }
        }), ContextCompat.GetMainExecutor(this)!);
    }

    private void BindUseCases()
    {
        if (_cameraProvider == null || _previewView == null) return;

        var preview = new Preview.Builder().Build()!;
        preview.SurfaceProvider = _previewView.SurfaceProvider;

        var analysis = new ImageAnalysis.Builder()
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
            .Build()!;
        analysis.SetAnalyzer(_analyzerExecutor!, _analyzer!);

        try
        {
            _cameraProvider.UnbindAll();
            _cameraProvider.BindToLifecycle(this, CameraSelector.DefaultBackCamera!, preview, analysis);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to bind CameraX use cases");
            Toast.MakeText(this, "Could not start camera", ToastLength.Long)?.Show();
            Finish();
        }
    }

    private void OnQrCodeDetected(object? sender, string raw)
    {
        // Frames arrive on the analyzer executor; marshal to UI to deliver the result.
        RunOnUiThread(() => DeliverResult(raw));
    }

    private void DeliverResult(string scanned)
    {
        if (_resultDelivered) return;
        _resultDelivered = true;

        var data = new Intent();
        data.PutExtra(ResultExtraScannedText, scanned);
        SetResult(Result.Ok, data);
        Finish();
    }

    private void PromptManualEntry()
    {
        var input = new TextInputEditText(this)
        {
            Hint = "npub1... / nprofile1... / hex"
        };
        input.SetSingleLine(true);
        input.InputType = InputTypes.ClassText;

        var layout = new TextInputLayout(this);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        var marginPx = (int)(20 * Resources!.DisplayMetrics!.Density);
        lp.SetMargins(marginPx, marginPx / 2, marginPx, 0);
        layout.LayoutParameters = lp;
        layout.AddView(input);

        new MaterialAlertDialogBuilder(this)
            .SetTitle("Enter pubkey")!
            .SetView(layout)!
            .SetPositiveButton("OK", (_, _) =>
            {
                var text = input.Text?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                    DeliverResult(text);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    protected override void OnDestroy()
    {
        try { _cameraProvider?.UnbindAll(); } catch { }
        try { _analyzerExecutor?.Shutdown(); } catch { }
        if (_analyzer != null)
        {
            _analyzer.QrCodeDetected -= OnQrCodeDetected;
            try { _analyzer.Dispose(); } catch { }
        }
        _analyzer = null;
        _analyzerExecutor = null;
        _cameraProvider = null;
        base.OnDestroy();
    }
}
