using Android.App;
using Android.Content;
using Android.Content.PM;
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
using OpenChat.Presentation.Services;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenChat.Android;

[Activity(Label = "OpenChat", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, Theme = "@style/AppTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class MainActivity : AppCompatActivity, IActivatableView
{
    // Static so services survive Activity.Recreate() (theme change)
    private static ShellViewModel? _shellViewModel;
    private static bool _servicesInitialized;
    private CompositeDisposable _disposables = new();

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Apply saved theme before calling base.OnCreate and SetContentView
        SetTheme(Services.ThemeService.GetSavedStyleResource(this));

        base.OnCreate(savedInstanceState);

        // Initialize logging (safe to call multiple times — guarded internally)
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OpenChat", "logs");
        var packageInfo = PackageManager?.GetPackageInfo(PackageName ?? "", 0);
        var appVersion = packageInfo?.VersionName ?? "unknown";
        LoggingConfiguration.Initialize(logDirectory: logDir, perSession: true, appVersion: appVersion);

        SetContentView(Resource.Layout.activity_main);

        // Create notification channels (safe to call multiple times)
        Services.RelayForegroundService.CreateNotificationChannel(this);
        Services.AndroidNotificationService.CreateChannel(this);

        // Set platform notification service for the orchestrator
        NotificationOrchestrator.NotificationService = new Services.AndroidNotificationService(this);

        if (!_servicesInitialized)
        {
            // Create services only once — they survive Activity.Recreate()
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
            _servicesInitialized = true;
        }

        // Observe login state and switch fragments
        _shellViewModel.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isLoggedIn =>
            {
                if (isLoggedIn && _shellViewModel.MainViewModel != null)
                {
                    ShowFragment(new ChatListFragment(_shellViewModel.MainViewModel, _shellViewModel), "chatlist");

                    // Auto-start relay foreground service if background mode is enabled
                    StartRelayServiceIfEnabled(_shellViewModel.MainViewModel);
                }
                else
                {
                    // Stop the foreground service on logout
                    Services.RelayForegroundService.Stop(this);
                    ShowFragment(new LoginFragment(_shellViewModel), "login");
                }
            })
            .DisposeWith(_disposables);

        // Show correct initial fragment based on current state
        // (handles Activity.Recreate() after theme change when already logged in)
        if (_shellViewModel.IsLoggedIn && _shellViewModel.MainViewModel != null)
            ShowFragment(new ChatListFragment(_shellViewModel.MainViewModel, _shellViewModel), "chatlist");
        else
            ShowFragment(new LoginFragment(_shellViewModel), "login");

        // Handle share intent on cold start
        HandleShareIntent(Intent);
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

    public void NavigateToAddBot()
    {
        if (_shellViewModel?.MainViewModel == null) return;
        _shellViewModel.MainViewModel.ChatListViewModel.AddBotCommand.Execute().Subscribe();
        var fragment = new AddBotFragment(_shellViewModel.MainViewModel);
        SupportFragmentManager.BeginTransaction()
            .Replace(Resource.Id.fragment_container, fragment, "addbot")
            .AddToBackStack("addbot")
            .Commit();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleShareIntent(intent);
    }

    /// <summary>
    /// Pending share intent extras, consumed by ChatFragment after navigation.
    /// </summary>
    internal static Bundle? PendingShareExtras { get; set; }

    private void HandleShareIntent(Intent? intent)
    {
        if (intent == null || !intent.GetBooleanExtra("shareAction", false))
            return;

        var chatId = intent.GetStringExtra("shareChatId");
        var accountPubKey = intent.GetStringExtra("shareAccountPubKey");
        if (string.IsNullOrEmpty(chatId)) return;

        // Store share extras for ChatFragment to pick up
        PendingShareExtras = intent.Extras;

        // If we need to switch accounts, do it
        if (!string.IsNullOrEmpty(accountPubKey) && _shellViewModel != null)
        {
            var currentAccount = AccountRegistryService.GetActiveAccount();
            if (currentAccount != null &&
                !string.Equals(currentAccount.PublicKeyHex, accountPubKey, StringComparison.OrdinalIgnoreCase))
            {
                // Switch account — this triggers full re-initialization
                _ = _shellViewModel.SwitchAccountAsync(accountPubKey);
                // Navigation to chat will happen after login state settles
                return;
            }
        }

        // Already on the right account — navigate to the chat
        NavigateToShareChat(chatId);
    }

    private void NavigateToShareChat(string chatId)
    {
        if (_shellViewModel?.MainViewModel == null) return;

        var chatListVm = _shellViewModel.MainViewModel.ChatListViewModel;
        var chatItem = chatListVm.Chats.FirstOrDefault(c => c.Id == chatId)
                    ?? chatListVm.AgentChats.FirstOrDefault(c => c.Id == chatId);

        if (chatItem != null)
        {
            chatListVm.SelectedChat = chatItem;
            NavigateToChat();
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        NotificationOrchestrator.IsAppInForeground = false;
    }

    protected override void OnResume()
    {
        base.OnResume();
        NotificationOrchestrator.IsAppInForeground = true;

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

    private void StartRelayServiceIfEnabled(MainViewModel mainVm)
    {
        // SettingsViewModel loads notification_mode in its constructor (LoadSettingsAsync).
        // Give it a moment to load, then check the mode.
        var settingsVm = mainVm.SettingsViewModel;
        settingsVm.WhenAnyValue(x => x.NotificationModeBackground)
            .Take(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isBackground =>
            {
                if (isBackground)
                    Services.RelayForegroundService.Start(this);
            })
            .DisposeWith(_disposables);
    }
}
