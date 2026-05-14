using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Scramble.Core.Configuration;
using Scramble.Core.Services;
using Scramble.MobileAndroid.Services;
using Scramble.MobileAndroid.Views;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;
using Scramble.UI.Services;

namespace Scramble.MobileAndroid;

/// <summary>
/// Avalonia Application for the Android head. Composes the shared ShellViewModel
/// (login + chat) with platform services and hosts the unified Avalonia UI from
/// Scramble.UI. The intent is to share UI/code 1:1 with desktop so this head can
/// be evaluated as a possible replacement for the native Scramble.Android app.
///
/// What's intentionally NOT wired here (yet):
///   - Real secure storage (uses PassThroughSecureStorage — see that class for context).
///   - Audio recording/playback, file picker, push notifications, QR scanner.
///   - Theme switching (defaults to NostrTheme via App.axaml StyleInclude).
/// Each will be added only when its absence blocks an evaluation milestone.
/// </summary>
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Profile-independent services. NostrService is cross-platform (Core);
            // clipboard / QR / launcher come from Scramble.UI's Avalonia implementations
            // which are framework-only and work fine on Android.
            ISecureStorage secureStorage = new MobileAndroidSecureStorage();
            var nostrService = new NostrService();
            var clipboard = new AvaloniaClipboard();
            var qrCodeGenerator = new AvaloniaQrCodeGenerator();
            var launcher = new MobileAndroidLauncher();
            var platform = new PlatformContext { IsMobile = true };

            var shellViewModel = new ShellViewModel(
                nostrService, secureStorage, clipboard, qrCodeGenerator, launcher, platform);

            // MLS service factory — Android cannot load the Rust uniffi backend (MlsService)
            // without the native libs cross-compiled for ARM; force Managed (pure-C#) here.
            shellViewModel.MlsServiceFactory = storage => new ManagedMlsService(storage);

            singleView.MainView = new MobileMainView
            {
                DataContext = shellViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
