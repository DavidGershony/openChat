using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;

namespace Scramble.MobileAndroid;

[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    protected MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // ReactiveUI.Avalonia 12.0.1 requires the callback overload (no parameterless variant).
        // The empty callback uses defaults; explicit configuration (exception handler etc.) can be
        // added when Android service composition is wired in.
        return base.CustomizeAppBuilder(builder)
            .UseReactiveUI(_ => { });
    }
}
