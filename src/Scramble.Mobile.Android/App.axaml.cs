using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Scramble.MobileAndroid;

/// <summary>
/// Minimal Avalonia Application for the Android head. Loads the same FluentTheme + NostrTheme styles
/// as Scramble.Desktop so the shared Avalonia views (Scramble.UI) render identically. Service
/// composition (secure storage, audio, notifications, MLS backend, etc.) is intentionally deferred
/// to a follow-up task: this head currently exists to prove the Avalonia stack compiles for Android.
/// </summary>
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // TODO: wire ShellViewModel + main view here once Android service composition is implemented.
            // For now the app launches with a blank single-view shell so we can verify the Avalonia
            // Android pipeline (resource loading, theming, JNI bridge) is healthy.
            singleView.MainView = new Avalonia.Controls.TextBlock
            {
                Text = "Scramble (Avalonia Android head) — composition TODO",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
