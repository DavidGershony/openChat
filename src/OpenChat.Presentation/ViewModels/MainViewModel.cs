using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.Services;

namespace OpenChat.Presentation.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger = LoggingConfiguration.CreateLogger<MainViewModel>();
    private readonly IMessageService _messageService;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;
    private readonly IPlatformClipboard _clipboard;

    [Reactive] public User? CurrentUser { get; set; }
    [Reactive] public bool IsLoggedIn { get; set; }
    [Reactive] public bool IsConnected { get; set; }

    [Reactive] public ViewModelBase? CurrentView { get; set; }
    [Reactive] public string HeaderDisplayName { get; set; } = "OpenChat";
    [Reactive] public bool IsHeaderLoading { get; set; }

    // Relay statuses
    public ObservableCollection<RelayStatusViewModel> RelayStatuses { get; } = new();

    // My Profile Dialog
    [Reactive] public bool ShowMyProfileDialog { get; set; }
    [Reactive] public string? MyNpub { get; set; }
    [Reactive] public string? CopyStatusMessage { get; set; }
    [Reactive] public string? MyDisplayName { get; set; }
    [Reactive] public string? MyName { get; set; }
    [Reactive] public string? MyPictureUrl { get; set; }
    [Reactive] public string? MyAbout { get; set; }
    [Reactive] public bool IsLoadingProfile { get; set; }

    public ChatListViewModel ChatListViewModel { get; }
    public ChatViewModel ChatViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public LoginViewModel LoginViewModel { get; }

    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatsCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ReactiveCommand<RelayStatusViewModel, Unit> ReconnectRelayCommand { get; }

    public MainViewModel(IMessageService messageService, INostrService nostrService, IStorageService storageService, IMlsService mlsService,
        IPlatformClipboard clipboard, IQrCodeGenerator qrCodeGenerator, IPlatformLauncher launcher)
    {
        _messageService = messageService;
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;
        _clipboard = clipboard;

        // Initialize child view models
        ChatListViewModel = new ChatListViewModel(messageService, storageService, mlsService, nostrService);
        ChatViewModel = new ChatViewModel(messageService, storageService, nostrService, mlsService, clipboard);
        SettingsViewModel = new SettingsViewModel(nostrService, storageService, mlsService, launcher);
        LoginViewModel = new LoginViewModel(nostrService, storageService, qrCodeGenerator);

        // Set up commands
        ShowSettingsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = SettingsViewModel;
        });

        ShowChatsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = null; // Main chat view
        });

        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);

        ShowMyProfileCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            MyNpub = CurrentUser?.Npub ?? "No key available";
            CopyStatusMessage = null;
            MyDisplayName = null;
            MyName = null;
            MyPictureUrl = null;
            MyAbout = null;
            ShowMyProfileDialog = true;

            // Fetch profile metadata from Nostr
            if (!string.IsNullOrEmpty(CurrentUser?.PublicKeyHex))
            {
                IsLoadingProfile = true;
                try
                {
                    var metadata = await _nostrService.FetchUserMetadataAsync(CurrentUser.PublicKeyHex);
                    if (metadata != null)
                    {
                        MyDisplayName = metadata.DisplayName;
                        MyName = metadata.Name;
                        MyPictureUrl = metadata.Picture;
                        MyAbout = metadata.About;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch profile metadata");
                }
                finally
                {
                    IsLoadingProfile = false;
                }
            }
        });

        CloseMyProfileCommand = ReactiveCommand.Create(() =>
        {
            ShowMyProfileDialog = false;
            CopyStatusMessage = null;
        });

        CopyNpubCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(MyNpub)) return;

            try
            {
                await _clipboard.SetTextAsync(MyNpub);
                CopyStatusMessage = "Copied to clipboard!";

                // Clear the message after 2 seconds
                await Task.Delay(2000);
                CopyStatusMessage = null;
            }
            catch
            {
                CopyStatusMessage = "Failed to copy";
            }
        });

        ReconnectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogInformation("Reconnecting to all relays...");
            var defaultRelays = new[]
            {
                "wss://relay.damus.io",
                "wss://nos.lol",
                "wss://relay.nostr.band"
            };

            await _nostrService.DisconnectAsync();
            await _nostrService.ConnectAsync(defaultRelays);

            // Subscriptions are now automatically re-sent by NostrService.ConnectToRelayAsync
            // via ResendSubscriptionsToRelayAsync, so no manual re-subscribe needed here.
        });

        ReconnectRelayCommand = ReactiveCommand.CreateFromTask<RelayStatusViewModel>(async relay =>
        {
            _logger.LogInformation("Reconnecting to relay: {RelayUrl}", relay.Url);
            relay.IsReconnecting = true;
            try
            {
                await _nostrService.ReconnectRelayAsync(relay.Url);
            }
            finally
            {
                relay.IsReconnecting = false;
            }
        });

        // Subscribe to login events
        LoginViewModel.WhenAnyValue(x => x.LoggedInUser)
            .Where(user => user != null)
            .Subscribe(user =>
            {
                CurrentUser = user;
                IsLoggedIn = true;
                InitializeAfterLoginAsync().ConfigureAwait(false);
            });

        // Subscribe to chat selection
        ChatListViewModel.WhenAnyValue(x => x.SelectedChat)
            .Where(chat => chat != null)
            .Subscribe(chat =>
            {
                ChatViewModel.LoadChat(chat!.Chat);
            });

        // Subscribe to connection status — track per-relay
        _nostrService.ConnectionStatus
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                var existing = RelayStatuses.FirstOrDefault(r => r.Url == status.RelayUrl);
                if (status.Removed)
                {
                    if (existing != null)
                        RelayStatuses.Remove(existing);
                }
                else if (existing != null)
                {
                    existing.IsConnected = status.IsConnected;
                    existing.Error = status.Error;
                }
                else if (!string.IsNullOrEmpty(status.RelayUrl))
                {
                    RelayStatuses.Add(new RelayStatusViewModel
                    {
                        Url = status.RelayUrl,
                        IsConnected = status.IsConnected,
                        Error = status.Error
                    });
                }
                IsConnected = RelayStatuses.Any(r => r.IsConnected);

                // Sync to SettingsViewModel relays
                var settingsRelay = SettingsViewModel.Relays.FirstOrDefault(r => r.Url == status.RelayUrl);
                if (settingsRelay != null)
                {
                    settingsRelay.IsConnected = status.IsConnected;
                    settingsRelay.Error = status.Error;
                }
            });

        // Check for existing user on startup
        InitializeAsync().ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        await _storageService.InitializeAsync();

        var currentUser = await _storageService.GetCurrentUserAsync();
        if (currentUser != null)
        {
            CurrentUser = currentUser;
            IsLoggedIn = true;
            await InitializeAfterLoginAsync();
        }
    }

    private async Task InitializeAfterLoginAsync()
    {
        if (CurrentUser == null) return;

        _logger.LogInformation("InitializeAfterLoginAsync starting for {Npub}", CurrentUser.Npub);

        try
        {
        // Show spinners immediately while everything initializes
        ChatListViewModel.IsLoading = true;
        IsHeaderLoading = true;

        _logger.LogDebug("Initializing message service");
        await _messageService.InitializeAsync();

        // Set user context on ChatViewModel for signing events
        if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
        {
            ChatViewModel.SetUserContext(CurrentUser.PrivateKeyHex, CurrentUser.PublicKeyHex);
        }

        // If using external signer (no private key), wire it into NostrService
        if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
        {
            _nostrService.SetExternalSigner(LoginViewModel.ExternalSigner);
        }

        // Initialize MLS service with user's keys (non-fatal if native library unavailable)
        // ManagedMlsService only needs the public key — pass empty private key for signer users
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            var mlsPrivateKey = CurrentUser.PrivateKeyHex ?? new string('0', 64);
            try
            {
                _logger.LogDebug("Initializing MLS service");
                await _mlsService.InitializeAsync(mlsPrivateKey, CurrentUser.PublicKeyHex);

                // Restore MLS service state (signing keys, stored KeyPackage) from persistence
                try
                {
                    var serviceState = await _storageService.GetMlsStateAsync("__service__");
                    if (serviceState != null)
                    {
                        await _mlsService.ImportServiceStateAsync(serviceState);
                        _logger.LogInformation("Restored MLS service state from persistence");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore MLS service state");
                }

                // Restore each group's MLS state from persistence
                try
                {
                    var allChats = await _storageService.GetAllChatsAsync();
                    foreach (var chat in allChats.Where(c => c.Type == ChatType.Group && c.MlsGroupId != null))
                    {
                        try
                        {
                            var hex = Convert.ToHexString(chat.MlsGroupId!).ToLowerInvariant();
                            var state = await _storageService.GetMlsStateAsync(hex);
                            if (state != null)
                            {
                                await _mlsService.ImportGroupStateAsync(chat.MlsGroupId!, state);
                                _logger.LogInformation("Restored MLS state for group {GroupId}", hex[..Math.Min(16, hex.Length)]);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to restore MLS state for group chat {ChatId}", chat.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate chats for MLS state restoration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLS initialization failed (native library may be unavailable). Continuing without MLS support.");
            }
        }

        // Connect to default relays
        _logger.LogInformation("Connecting to default relays");
        var defaultRelays = new[]
        {
            "wss://relay.damus.io",
            "wss://nos.lol",
            "wss://relay.nostr.band"
        };

        await _nostrService.ConnectAsync(defaultRelays);
        _logger.LogInformation("Relay connection initiated");

        // Subscribe to Welcome messages (incoming group invites)
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            _logger.LogDebug("Subscribing to welcome messages");
            await _nostrService.SubscribeToWelcomesAsync(CurrentUser.PublicKeyHex);
        }

        // Check if our KeyPackage is published on relays
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            try
            {
                _logger.LogDebug("Checking for published KeyPackage on relays");
                var myKeyPackages = await _nostrService.FetchKeyPackagesAsync(CurrentUser.PublicKeyHex);
                if (!myKeyPackages.Any())
                {
                    _logger.LogWarning("No KeyPackage found on relays for current user");
                    ChatListViewModel.StatusMessage = "No KeyPackage published — others cannot invite you. Publish one in Settings.";
                }
                else
                {
                    _logger.LogInformation("Found {Count} KeyPackage(s) on relays", myKeyPackages.Count());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check KeyPackage status on relays");
            }
        }

        // Fetch own profile metadata for avatar
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            try
            {
                _logger.LogDebug("Fetching own profile metadata");
                var metadata = await _nostrService.FetchUserMetadataAsync(CurrentUser.PublicKeyHex);
                if (metadata != null)
                {
                    MyDisplayName = metadata.DisplayName;
                    MyName = metadata.Name;
                    MyPictureUrl = metadata.Picture;
                    MyAbout = metadata.About;
                    HeaderDisplayName = metadata.GetDisplayName();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch own profile metadata at login");
            }
            finally
            {
                IsHeaderLoading = false;
            }
        }
        else
        {
            IsHeaderLoading = false;
        }

        // Update SettingsViewModel with user data (LoadSettingsAsync ran before login)
        SettingsViewModel.PublicKeyHex = CurrentUser.PublicKeyHex;
        SettingsViewModel.PrivateKeyHex = CurrentUser.PrivateKeyHex;
        SettingsViewModel.Npub = CurrentUser.Npub;
        SettingsViewModel.DisplayName = CurrentUser.DisplayName;
        SettingsViewModel.Username = CurrentUser.Username;
        SettingsViewModel.About = CurrentUser.About;
        SettingsViewModel.AvatarUrl = CurrentUser.AvatarUrl;

        // Load chats
        _logger.LogDebug("Loading chats");
        await ChatListViewModel.LoadChatsAsync();

        // Subscribe to group messages for existing groups
        var chats = await _messageService.GetChatsAsync();
        var groupIds = chats
            .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
            .Select(c => Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant())
            .ToList();

        if (groupIds.Count > 0)
        {
            _logger.LogDebug("Subscribing to {Count} group message channels", groupIds.Count);
            var latestActivity = chats
                .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null)
                .Select(c => c.LastActivityAt)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            DateTimeOffset? since = latestActivity > DateTime.MinValue
                ? new DateTimeOffset(latestActivity, TimeSpan.Zero).AddMinutes(-5)
                : null;
            await _nostrService.SubscribeToGroupMessagesAsync(groupIds, since);
        }

        _logger.LogInformation("InitializeAfterLoginAsync completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeAfterLoginAsync failed");
        }
    }

    private async Task LogoutAsync()
    {
        await _nostrService.DisconnectAsync();
        CurrentUser = null;
        IsLoggedIn = false;
        HeaderDisplayName = "OpenChat";
        RelayStatuses.Clear();
        ChatListViewModel.ClearChats();
        ChatViewModel.ClearChat();
    }
}

public class RelayStatusViewModel : ViewModelBase
{
    [Reactive] public string Url { get; set; } = string.Empty;
    [Reactive] public bool IsConnected { get; set; }
    [Reactive] public string? Error { get; set; }
    [Reactive] public bool IsReconnecting { get; set; }

    /// <summary>Short display name: strips wss:// and trailing /</summary>
    public string DisplayName => Url.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
}
