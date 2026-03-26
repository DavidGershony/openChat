using System;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;
using OpenChat.UI.Views;
using OpenChat.UI.Services;
using OpenChat.Presentation.ViewModels;
using OpenChat.Core.Services;
using ReactiveUI;

namespace OpenChat.Desktop;

public partial class App : Application
{
    private static ILogger? _logger;

    public override void Initialize()
    {
        // Initialize logging first, before anything else
        LoggingConfiguration.Initialize(logDirectory: ProfileConfiguration.LogDirectory);
        _logger = LoggingConfiguration.CreateLogger("App");

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Set up RxUI exception handler
        RxApp.DefaultExceptionHandler = Observer.Create<Exception>(OnRxException);

        _logger.LogInformation("Global exception handlers registered");

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _logger?.LogInformation("OnFrameworkInitializationCompleted starting");

        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _logger?.LogDebug("Creating profile-independent services...");

                // Profile-independent services (survive across logins)
                var secureStorage = new DesktopSecureStorage();
                var nostrService = new NostrService();
                var clipboard = new AvaloniaClipboard();
                var qrCodeGenerator = new AvaloniaQrCodeGenerator();
                var launcher = new AvaloniaLauncher();

                // Audio and upload services (profile-independent, set as static on ChatViewModel)
                var audioRecording = new DesktopAudioRecordingService();
                var audioPlayback = new DesktopAudioPlaybackService();
                var blossomUpload = new BlossomUploadService();
                ChatViewModel.AudioRecordingService = audioRecording;
                ChatViewModel.AudioPlaybackService = audioPlayback;
                ChatViewModel.MediaUploadService = blossomUpload;

                // File picker (needs desktop.MainWindow, set after window creation)
                // Will be set below after MainWindow is created.

                _logger?.LogDebug("Creating ShellViewModel...");
                var shellViewModel = new ShellViewModel(nostrService, secureStorage, clipboard, qrCodeGenerator, launcher);

                // MLS service factory — platform-specific backend selection
                shellViewModel.MlsServiceFactory = storage =>
                    ProfileConfiguration.ActiveMdkBackend == MdkBackend.Managed
                        ? new ManagedMlsService(storage)
                        : new MlsService(storage);
                _logger?.LogInformation("Using {Backend} MLS backend", ProfileConfiguration.ActiveMdkBackend);

                _logger?.LogDebug("Creating MainWindow...");
                desktop.MainWindow = new MainWindow
                {
                    DataContext = shellViewModel
                };
                desktop.MainWindow.Title = "OpenChat" + ProfileConfiguration.WindowTitleSuffix;

                // File picker for image/media attach (needs MainWindow reference)
                ChatViewModel.FilePickerFunc = async () =>
                {
                    var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
                    if (topLevel == null) return null;

                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Select image to send",
                            AllowMultiple = false,
                            FileTypeFilter = new[]
                            {
                                new Avalonia.Platform.Storage.FilePickerFileType("Images")
                                {
                                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" },
                                    MimeTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }
                                },
                                Avalonia.Platform.Storage.FilePickerFileTypes.All
                            }
                        });

                    if (files.Count == 0) return null;

                    var file = files[0];
                    await using var stream = await file.OpenReadAsync();
                    using var ms = new System.IO.MemoryStream();
                    await stream.CopyToAsync(ms);
                    var data = ms.ToArray();

                    var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "application/octet-stream"
                    };

                    return (data, file.Name, mime);
                };

                _logger?.LogInformation("MainWindow created successfully (Profile: {Profile})", ProfileConfiguration.ProfileName);

                // Handle application shutdown
                desktop.ShutdownRequested += (_, _) =>
                {
                    _logger?.LogInformation("Application shutdown requested");
                    LoggingConfiguration.Shutdown();
                };
            }

            base.OnFrameworkInitializationCompleted();
            _logger?.LogInformation("OnFrameworkInitializationCompleted completed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "FATAL: Exception in OnFrameworkInitializationCompleted");
            LoggingConfiguration.Shutdown();
            throw;
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _logger?.LogCritical(exception, "FATAL: Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
        LoggingConfiguration.Shutdown();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved(); // Prevent crash
    }

    private static void OnRxException(Exception ex)
    {
        _logger?.LogError(ex, "RxUI exception caught");
    }
}

// Helper for RxUI exception handler
internal static class Observer
{
    public static IObserver<T> Create<T>(Action<T> onNext) => new AnonymousObserver<T>(onNext);

    private class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _onNext(value);
    }
}
