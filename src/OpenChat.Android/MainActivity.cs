using Android.App;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using Fragment = AndroidX.Fragment.App.Fragment;
using Google.Android.Material.BottomNavigation;
using OpenChat.Android.Fragments;
using OpenChat.Android.Services;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;
using OpenChat.Core.Services;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenChat.Android;

[Activity(Label = "OpenChat", MainLauncher = true, Theme = "@style/AppTheme")]
public class MainActivity : AppCompatActivity, IActivatableView
{
    private MainViewModel? _viewModel;
    private CompositeDisposable _disposables = new();

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Initialize logging
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OpenChat", "logs");
        LoggingConfiguration.Initialize(logDirectory: logDir);

        SetContentView(Resource.Layout.activity_main);

        // Create services
        var dbPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OpenChat", "openchat.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);

        var secureStorage = new AndroidSecureStorage();
        var storageService = new StorageService(dbPath, secureStorage);
        var nostrService = new NostrService();
        IMlsService mlsService = new ManagedMlsService(storageService);
        var messageService = new MessageService(storageService, nostrService, mlsService);

        // Create platform services
        var clipboard = new AndroidClipboardService(this);
        var qrCodeGenerator = new AndroidQrCodeGenerator();
        var launcher = new AndroidLauncher(this);

        // Create MainViewModel
        _viewModel = new MainViewModel(
            messageService, nostrService, storageService, mlsService,
            clipboard, qrCodeGenerator, launcher);

        // Observe login state and switch fragments
        _viewModel.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isLoggedIn =>
            {
                if (isLoggedIn)
                {
                    ShowFragment(new ChatListFragment(_viewModel), "chatlist");
                }
                else
                {
                    ShowFragment(new LoginFragment(_viewModel), "login");
                }
            })
            .DisposeWith(_disposables);

        // Start with login or chat list based on initial state
        if (_viewModel.IsLoggedIn)
        {
            ShowFragment(new ChatListFragment(_viewModel), "chatlist");
        }
        else
        {
            ShowFragment(new LoginFragment(_viewModel), "login");
        }
    }

    private void ShowFragment(Fragment fragment, string tag)
    {
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, tag)
            .Commit();
    }

    public void NavigateToChat()
    {
        if (_viewModel == null) return;
        var fragment = new ChatFragment(_viewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "chat")
            .AddToBackStack("chat")
            .Commit();
    }

    public void NavigateToSettings()
    {
        if (_viewModel == null) return;
        var fragment = new SettingsFragment(_viewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "settings")
            .AddToBackStack("settings")
            .Commit();
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Reconnect external signer WebSocket after returning from background
        // (e.g. user switched to Amber to approve, then came back)
        var signer = _viewModel?.LoginViewModel?.ExternalSigner;
        if (signer != null && !signer.IsConnected)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await signer.ReconnectAsync();
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("OpenChat", $"Signer reconnect failed: {ex.Message}");
                }
            });
        }
    }

    protected override void OnDestroy()
    {
        _disposables.Dispose();
        base.OnDestroy();
    }
}
