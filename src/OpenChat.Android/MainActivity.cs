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
    private ShellViewModel? _shellViewModel;
    private CompositeDisposable _disposables = new();

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Apply saved theme before calling base.OnCreate and SetContentView
        SetTheme(Services.ThemeService.GetSavedStyleResource(this));

        base.OnCreate(savedInstanceState);

        // Initialize logging
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OpenChat", "logs");
        LoggingConfiguration.Initialize(logDirectory: logDir, perSession: true);

        SetContentView(Resource.Layout.activity_main);

        // Create services (StorageService handles its own directory creation)
        var secureStorage = new AndroidSecureStorage();
        var nostrService = new NostrService();

        // Create platform services
        var clipboard = new AndroidClipboardService(this);
        var qrCodeGenerator = new AndroidQrCodeGenerator();
        var launcher = new AndroidLauncher(this);

        // Audio and upload services for voice messages
        var audioRecording = new OpenChat.Android.Services.AndroidAudioRecordingService(this);
        var audioPlayback = new OpenChat.Android.Services.AndroidAudioPlaybackService(this);
        var blossomUpload = new BlossomUploadService();
        ChatViewModel.AudioRecordingService = audioRecording;
        ChatViewModel.AudioPlaybackService = audioPlayback;
        ChatViewModel.MediaUploadService = blossomUpload;

        // Create ShellViewModel (manages login → service creation → MainViewModel lifecycle)
        _shellViewModel = new ShellViewModel(nostrService, secureStorage, clipboard, qrCodeGenerator, launcher);
        _shellViewModel.MlsServiceFactory = storage => new ManagedMlsService(storage);

        // Observe login state and switch fragments
        _shellViewModel.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isLoggedIn =>
            {
                if (isLoggedIn && _shellViewModel.MainViewModel != null)
                {
                    ShowFragment(new ChatListFragment(_shellViewModel.MainViewModel), "chatlist");
                }
                else
                {
                    ShowFragment(new LoginFragment(_shellViewModel), "login");
                }
            })
            .DisposeWith(_disposables);

        // Start with login fragment
        ShowFragment(new LoginFragment(_shellViewModel), "login");
    }

    private void ShowFragment(Fragment fragment, string tag)
    {
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, tag)
            .Commit();
    }

    public void NavigateToChat()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        var fragment = new ChatFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "chat")
            .AddToBackStack("chat")
            .Commit();
    }

    public void NavigateToSettings()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        var fragment = new SettingsFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "settings")
            .AddToBackStack("settings")
            .Commit();
    }

    public void NavigateToNewChat()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        // Execute command to clear state and populate relay selection list
        _shellViewModel.MainViewModel.ChatListViewModel.NewChatCommand.Execute().Subscribe();
        var fragment = new NewChatFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "newchat")
            .AddToBackStack("newchat")
            .Commit();
    }

    public void NavigateToNewGroup()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        // Execute command to clear state and populate relay selection list
        _shellViewModel.MainViewModel.ChatListViewModel.NewGroupCommand.Execute().Subscribe();
        var fragment = new NewGroupFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "newgroup")
            .AddToBackStack("newgroup")
            .Commit();
    }

    public void NavigateToAddBot()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        var fragment = new AddBotFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "addbot")
            .AddToBackStack("addbot")
            .Commit();
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Reconnect relay WebSockets after returning from background
        // (Android suspends network connections when app is backgrounded)
        if (_shellViewModel?.MainViewModel != null && _shellViewModel.IsLoggedIn)
        {
            var mainVm = _shellViewModel.MainViewModel;
            mainVm.ChatListViewModel.StatusMessage = "Reconnecting to relays...";

            _ = Task.Run(async () =>
            {
                try
                {
                    await mainVm.ReconnectCommand.Execute();
                    RunOnUiThread(() =>
                    {
                        var connected = mainVm.RelayStatuses.Count(r => r.IsConnected);
                        var total = mainVm.RelayStatuses.Count;
                        mainVm.ChatListViewModel.StatusMessage = $"Connected to {connected}/{total} relays";
                    });
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("OpenChat", $"Relay reconnect failed: {ex.Message}");
                    RunOnUiThread(() =>
                    {
                        mainVm.ChatListViewModel.StatusMessage = "Relay reconnect failed";
                    });
                }
            });
        }

        // Reconnect external signer WebSocket after returning from background
        // (e.g. user switched to Amber to approve, then came back)
        var signer = _shellViewModel?.LoginViewModel?.ExternalSigner;
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
