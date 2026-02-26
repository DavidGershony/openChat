using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.UI.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger = LoggingConfiguration.CreateLogger<MainViewModel>();
    private readonly IMessageService _messageService;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;

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

    public MainViewModel(IMessageService messageService, INostrService nostrService, IStorageService storageService, IMlsService mlsService)
    {
        _messageService = messageService;
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;

        // Initialize child view models
        ChatListViewModel = new ChatListViewModel(messageService, storageService, mlsService, nostrService);
        ChatViewModel = new ChatViewModel(messageService, storageService, nostrService, mlsService);
        SettingsViewModel = new SettingsViewModel(nostrService, storageService, mlsService);
        LoginViewModel = new LoginViewModel(nostrService, storageService);

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
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var clipboard = desktop.MainWindow?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(MyNpub);
                        CopyStatusMessage = "Copied to clipboard!";

                        // Clear the message after 2 seconds
                        await Task.Delay(2000);
                        CopyStatusMessage = null;
                    }
                }
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

        // Show spinners immediately while everything initializes
        ChatListViewModel.IsLoading = true;
        IsHeaderLoading = true;

        await _messageService.InitializeAsync();

        // Set user context on ChatViewModel for signing events
        if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
        {
            ChatViewModel.SetUserContext(CurrentUser.PrivateKeyHex, CurrentUser.PublicKeyHex);
        }

        // Initialize MLS service with user's keys
        if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && !string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            await _mlsService.InitializeAsync(CurrentUser.PrivateKeyHex, CurrentUser.PublicKeyHex);

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

        // Connect to default relays
        var defaultRelays = new[]
        {
            "wss://relay.damus.io",
            "wss://nos.lol",
            "wss://relay.nostr.band"
        };

        await _nostrService.ConnectAsync(defaultRelays);

        // Subscribe to Welcome messages (incoming group invites)
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            await _nostrService.SubscribeToWelcomesAsync(CurrentUser.PublicKeyHex);
        }

        // Publish our KeyPackage so others can invite us
        if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
        {
            try
            {
                var keyPackage = await _mlsService.GenerateKeyPackageAsync();
                await _nostrService.PublishKeyPackageAsync(keyPackage.Data, CurrentUser.PrivateKeyHex, keyPackage.NostrTags);
            }
            catch (Exception ex)
            {
                // KeyPackage publishing is optional - log and continue
                _logger.LogError(ex, "Failed to publish KeyPackage");
            }
        }

        // Fetch own profile metadata for avatar
        if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
        {
            try
            {
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

        // Load chats
        await ChatListViewModel.LoadChatsAsync();

        // Subscribe to group messages for existing groups
        var chats = await _messageService.GetChatsAsync();
        var groupIds = chats
            .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
            .Select(c => Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant())
            .ToList();

        if (groupIds.Count > 0)
        {
            await _nostrService.SubscribeToGroupMessagesAsync(groupIds);
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
