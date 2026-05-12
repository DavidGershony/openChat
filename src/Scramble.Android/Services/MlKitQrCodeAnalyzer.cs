using AndroidX.Camera.Core;
using Android.Gms.Tasks;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.BarCode;
using Xamarin.Google.MLKit.Vision.Common;

namespace Scramble.Android.Services;

/// <summary>
/// CameraX <see cref="ImageAnalysis.IAnalyzer"/> that runs each frame through MLKit's
/// barcode scanner and surfaces the first QR-format payload via <see cref="QrCodeDetected"/>.
///
/// Frames arrive on CameraX's analyzer executor thread, so the event is raised off the UI
/// thread; subscribers are responsible for marshalling.
///
/// Identical payloads decoded back-to-back within <see cref="DebounceWindow"/> are suppressed
/// to avoid hammering the host activity while the camera is still pointed at the same code
/// (Damus/Primal pattern).
/// </summary>
public sealed class MlKitQrCodeAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    private readonly IBarcodeScanner _scanner;
    private string? _lastPayload;
    private DateTime _lastPayloadAt = DateTime.MinValue;

    public event EventHandler<string>? QrCodeDetected;

    public MlKitQrCodeAnalyzer()
    {
        // QR-only — narrowing the format set lets MLKit skip other detectors and saves CPU.
        var options = new BarcodeScannerOptions.Builder()
            .SetBarcodeFormats(Barcode.FormatQrCode)
            .Build();
        _scanner = BarcodeScanning.GetClient(options);
    }

    public void Analyze(IImageProxy? imageProxy)
    {
        if (imageProxy == null) return;
        var mediaImage = imageProxy.Image;
        if (mediaImage == null)
        {
            imageProxy.Close();
            return;
        }

        var rotation = imageProxy.ImageInfo?.RotationDegrees ?? 0;
        var input = InputImage.FromMediaImage(mediaImage, rotation);

        _scanner.Process(input)
            .AddOnSuccessListener(new SuccessListener(this))
            .AddOnCompleteListener(new CompleteListener(imageProxy));
    }

    private void OnSuccess(Java.Lang.Object? result)
    {
        if (result is not global::Java.Util.IList list) return;

        var size = list.Size();
        for (int i = 0; i < size; i++)
        {
            if (list.Get(i) is not Barcode barcode) continue;
            var raw = barcode.RawValue;
            if (string.IsNullOrEmpty(raw)) continue;

            var now = DateTime.UtcNow;
            if (raw == _lastPayload && (now - _lastPayloadAt) < DebounceWindow)
                return;

            _lastPayload = raw;
            _lastPayloadAt = now;
            QrCodeDetected?.Invoke(this, raw);
            return;
        }
    }

    private sealed class SuccessListener : Java.Lang.Object, IOnSuccessListener
    {
        private readonly MlKitQrCodeAnalyzer _owner;
        public SuccessListener(MlKitQrCodeAnalyzer owner) => _owner = owner;
        public void OnSuccess(Java.Lang.Object? result) => _owner.OnSuccess(result);
    }

    private sealed class CompleteListener : Java.Lang.Object, IOnCompleteListener
    {
        private readonly IImageProxy _imageProxy;
        public CompleteListener(IImageProxy imageProxy) => _imageProxy = imageProxy;
        public void OnComplete(global::Android.Gms.Tasks.Task task) => _imageProxy.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _scanner.Close(); } catch { }
        }
        base.Dispose(disposing);
    }
}
