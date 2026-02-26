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
                _logger?.LogDebug("Creating services...");
                var storageService = new StorageService(ProfileConfiguration.DatabasePath);
                var nostrService = new NostrService();
                // ManagedMlsService disabled until marmut-mdk NuGet packages are published
                // IMlsService mlsService = ProfileConfiguration.ActiveMdkBackend == MdkBackend.Managed
                //     ? new ManagedMlsService(storageService)
                //     : new MlsService();
                IMlsService mlsService = new MlsService();
                _logger?.LogInformation("Using Rust MLS backend (managed backend temporarily disabled)");
                var messageService = new MessageService(storageService, nostrService, mlsService);

                _logger?.LogDebug("Creating platform services...");
                var clipboard = new AvaloniaClipboard();
                var qrCodeGenerator = new AvaloniaQrCodeGenerator();
                var launcher = new AvaloniaLauncher();

                _logger?.LogDebug("Creating MainViewModel...");
                var mainViewModel = new MainViewModel(messageService, nostrService, storageService, mlsService, clipboard, qrCodeGenerator, launcher);

                _logger?.LogDebug("Creating MainWindow...");
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };
                desktop.MainWindow.Title = "OpenChat" + ProfileConfiguration.WindowTitleSuffix;

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
