using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.Logging;
using OpenChat.Core;
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
    private readonly IQrCodeGenerator _qrCodeGenerator;

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
    [Reactive] public string? MyNsec { get; set; }
    [Reactive] public string? CopyStatusMessage { get; set; }
    [Reactive] public string? MyDisplayName { get; set; }
    [Reactive] public string? MyName { get; set; }
    [Reactive] public string? MyPictureUrl { get; set; }
    [Reactive] public string? MyAbout { get; set; }
    [Reactive] public bool IsLoadingProfile { get; set; }
    [Reactive] public byte[]? MyNpubQrPngBytes { get; set; }

    private readonly Action? _onLogoutRequested;

    /// <summary>
    /// External signer instance, set by ShellViewModel when the user logged in via Amber/NIP-46.
    /// </summary>
    public IExternalSigner? ExternalSigner { get; set; }

    public ChatListViewModel ChatListViewModel { get; }
    public ChatViewModel ChatViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatsCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseMyProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNsecCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ReactiveCommand<RelayStatusViewModel, Unit> ReconnectRelayCommand { get; }

    public MainViewModel(IMessageService messageService, INostrService nostrService, IStorageService storageService, IMlsService mlsService,
        IPlatformClipboard clipboard, IQrCodeGenerator qrCodeGenerator, IPlatformLauncher launcher,
        Action? onLogoutRequested = null)
    {
        _messageService = messageService;
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;
        _clipboard = clipboard;
        _qrCodeGenerator = qrCodeGenerator;
        _onLogoutRequested = onLogoutRequested;

        // Initialize child view models
        ChatListViewModel = new ChatListViewModel(messageService, storageService, mlsService, nostrService);
        ChatViewModel = new ChatViewModel(messageService, storageService, nostrService, mlsService, clipboard);
        SettingsViewModel = new SettingsViewModel(nostrService, storageService, mlsService, messageService, launcher);

        // Set up commands
        ShowSettingsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = SettingsViewModel;
        });

        ShowChatsCommand = ReactiveCommand.Create(() =>
        {
            CurrentView = null; // Main chat view

            // Sync relay statuses with any changes made in settings
            RefreshRelayStatusesFromSettings();

            // Update header name from settings if changed
            var name = SettingsViewModel.DisplayName ?? SettingsViewModel.Username;
            if (!string.IsNullOrEmpty(name))
            {
                HeaderDisplayName = name;
            }
        });

        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);

        ShowMyProfileCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogInformation("ShowMyProfileCommand executed - opening profile dialog");
            MyNpub = CurrentUser?.Npub ?? "No key available";
            MyNsec = CurrentUser?.Nsec; // null for external signer users
            CopyStatusMessage = null;
            MyDisplayName = null;
            MyName = null;
            MyPictureUrl = null;
            MyAbout = null;
            MyNpubQrPngBytes = _qrCodeGenerator.GeneratePng(MyNpub, 10);
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
            MyNpubQrPngBytes = null;
        });

        CopyNpubCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(MyNpub)) return;

            try
            {
                await _clipboard.SetTextAsync(MyNpub);
                CopyStatusMessage = "Copied npub to clipboard!";

                await Task.Delay(2000);
                CopyStatusMessage = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy npub to clipboard");
                CopyStatusMessage = "Failed to copy";
            }
        });

        CopyNsecCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(MyNsec)) return;

            try
            {
                await _clipboard.SetTextAsync(MyNsec);
                CopyStatusMessage = "Copied nsec to clipboard! Clear your clipboard soon.";
                _logger.LogInformation("User copied nsec to clipboard");

                await Task.Delay(3000);
                CopyStatusMessage = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy nsec to clipboard");
                CopyStatusMessage = "Failed to copy";
            }
        });

        ReconnectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogInformation("Reconnecting to all relays...");
            var relays = NostrConstants.DefaultRelays.ToList();

            // Try to use saved relay list
            if (CurrentUser != null)
            {
                var saved = await _storageService.GetUserRelayListAsync(CurrentUser.PublicKeyHex);
                if (saved.Count > 0)
                    relays = saved.Select(r => r.Url).Distinct().ToList();
            }

            await _nostrService.DisconnectAsync();
            await _nostrService.ConnectAsync(relays);

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

    }

    public async Task InitializeAfterLoginAsync()
    {
        if (CurrentUser == null) return;

        _logger.LogInformation("InitializeAfterLoginAsync starting for {Npub}...", CurrentUser.Npub?[..Math.Min(12, CurrentUser.Npub.Length)]);

        try
        {
        // Show the npub immediately so user knows which identity is connected
        HeaderDisplayName = CurrentUser.Npub != null && CurrentUser.Npub.Length > 16
            ? CurrentUser.Npub[..16] + "..."
            : CurrentUser.Npub ?? "OpenChat";

        // Show spinners immediately while everything initializes
        ChatListViewModel.IsLoading = true;
        IsHeaderLoading = true;

        _logger.LogDebug("Initializing message service");
        await _messageService.InitializeAsync();

        // Set user context on ChatViewModel for signing events and contact resolution.
        // For signer users, PrivateKeyHex is null but PublicKeyHex is still needed.
        ChatViewModel.SetUserContext(CurrentUser.PrivateKeyHex, CurrentUser.PublicKeyHex);

        // If using external signer (no private key), wire it into NostrService.
        // First, try auto-reconnect from persisted signer session if not already connected.
        if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && ExternalSigner?.IsConnected != true)
        {
            if (!string.IsNullOrEmpty(CurrentUser.SignerRelayUrl) &&
                !string.IsNullOrEmpty(CurrentUser.SignerRemotePubKey) &&
                !string.IsNullOrEmpty(CurrentUser.SignerLocalPrivateKeyHex) &&
                !string.IsNullOrEmpty(CurrentUser.SignerLocalPublicKeyHex))
            {
                _logger.LogInformation("Restoring signer session (relay and keys redacted)");

                try
                {
                    // Restore session using persisted ephemeral keypair — no connect request needed,
                    // Amber already authorized this keypair during initial login
                    var connected = await ExternalSigner!.RestoreSessionAsync(
                        CurrentUser.SignerRelayUrl,
                        CurrentUser.SignerRemotePubKey,
                        CurrentUser.SignerLocalPrivateKeyHex,
                        CurrentUser.SignerLocalPublicKeyHex,
                        CurrentUser.SignerSecret);

                    if (connected)
                    {
                        _logger.LogInformation("Signer session restored successfully");
                    }
                    else
                    {
                        _logger.LogWarning("Signer session restore failed — signer may be offline. Continuing without signer.");
                        ChatListViewModel.StatusMessage = "Signer disconnected. Restart your signer app.";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Signer session restore failed");
                    ChatListViewModel.StatusMessage = "Signer restore failed. Restart your signer app.";
                }
            }
            else
            {
                _logger.LogWarning("Signer user has no persisted session details — cannot auto-reconnect");
            }
        }

        if (string.IsNullOrEmpty(CurrentUser.PrivateKeyHex) && ExternalSigner?.IsConnected == true)
        {
            _nostrService.SetExternalSigner(ExternalSigner);
            ChatViewModel.MediaUploadService?.SetExternalSigner(ExternalSigner);
            _logger.LogInformation("External signer wired to NostrService and MediaUploadService");
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

                // Wire up the Nostr event signer for kind 445 MLS group messages.
                // Local key users sign with their private key; external signer users delegate to Amber/NIP-46.
                if (!string.IsNullOrEmpty(CurrentUser.PrivateKeyHex))
                {
                    _mlsService.SetNostrEventSigner(new LocalNostrEventSigner(CurrentUser.PrivateKeyHex));
                    _logger.LogInformation("MLS event signer set to LocalNostrEventSigner");
                }
                else if (ExternalSigner?.IsConnected == true)
                {
                    _mlsService.SetNostrEventSigner(new ExternalNostrEventSigner(ExternalSigner));
                    _logger.LogInformation("MLS event signer set to ExternalNostrEventSigner");
                }
                else
                {
                    _logger.LogWarning("No Nostr event signer available — kind 445 signing will fall back to local key in ManagedMlsService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLS initialization failed (native library may be unavailable). Continuing without MLS support.");
            }
        }

        // Update SettingsViewModel with user data
        SettingsViewModel.PublicKeyHex = CurrentUser.PublicKeyHex;
        SettingsViewModel.PrivateKeyHex = CurrentUser.PrivateKeyHex;
        SettingsViewModel.Npub = CurrentUser.Npub;
        SettingsViewModel.DisplayName = CurrentUser.DisplayName;
        SettingsViewModel.Username = CurrentUser.Username;
        SettingsViewModel.About = CurrentUser.About;
        SettingsViewModel.AvatarUrl = CurrentUser.AvatarUrl;

        // === FAST PATH: Show chats from DB immediately ===
        _logger.LogDebug("Loading chats from local database");
        await ChatListViewModel.LoadChatsAsync();
        _logger.LogInformation("Chat list loaded from DB — UI is now responsive");

        // === BACKGROUND: All relay/network operations run without blocking the UI ===
        _ = InitializeNetworkAsync();

        _logger.LogInformation("InitializeAfterLoginAsync completed (network init continuing in background)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeAfterLoginAsync failed");
        }
    }

    private void RefreshRelayStatusesFromSettings()
    {
        var settingsRelays = SettingsViewModel.Relays;

        // Remove relay statuses that no longer exist in settings
        var toRemove = RelayStatuses.Where(rs => !settingsRelays.Any(sr => sr.Url == rs.Url)).ToList();
        foreach (var item in toRemove)
        {
            RelayStatuses.Remove(item);
            _logger.LogInformation("Removed relay status for {Url} (removed in settings)", item.Url);
        }

        // Add relay statuses for new relays added in settings
        foreach (var sr in settingsRelays)
        {
            if (!RelayStatuses.Any(rs => rs.Url == sr.Url))
            {
                RelayStatuses.Add(new RelayStatusViewModel
                {
                    Url = sr.Url,
                    IsConnected = sr.IsConnected,
                    Error = sr.Error
                });
                _logger.LogInformation("Added relay status for {Url} (added in settings)", sr.Url);
            }
        }

        IsConnected = RelayStatuses.Any(r => r.IsConnected);
    }

    /// <summary>
    /// Runs all relay/network operations in the background after the UI is already showing cached chats.
    /// </summary>
    private async Task InitializeNetworkAsync()
    {
        try
        {
            // 1. Determine relays to connect to (from cache, then network)
            var relaysToConnect = NostrConstants.DefaultRelays.ToList();
            var shouldPublishNip65 = false;
            try
            {
                var savedRelays = await _storageService.GetUserRelayListAsync(CurrentUser!.PublicKeyHex);
                if (savedRelays.Count > 0)
                {
                    _logger.LogInformation("Using {Count} saved relays from local storage", savedRelays.Count);
                    relaysToConnect = savedRelays.Select(r => r.Url).Distinct().ToList();
                }
                else
                {
                    _logger.LogInformation("No saved relays, fetching NIP-65 relay list from discovery relays");
                    var discoveredRelays = await _nostrService.FetchRelayListAsync(CurrentUser.PublicKeyHex);
                    if (discoveredRelays.Count > 0)
                    {
                        _logger.LogInformation("Discovered {Count} relays via NIP-65", discoveredRelays.Count);
                        await _storageService.SaveUserRelayListAsync(CurrentUser.PublicKeyHex, discoveredRelays);
                        relaysToConnect = discoveredRelays.Select(r => r.Url).Distinct().ToList();
                    }
                    else
                    {
                        // New key with no relay list anywhere — publish defaults after connecting
                        shouldPublishNip65 = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch relay list, using defaults");
            }

            // 2. Set auth credentials before connecting so NIP-42 challenges can be answered
            _nostrService.SetAuthCredentials(CurrentUser.PrivateKeyHex);

            _logger.LogInformation("Connecting to {Count} relays", relaysToConnect.Count);
            await _nostrService.ConnectAsync(relaysToConnect);

            // 3. Publish default NIP-65 relay list for new keys (must happen after connect)
            if (shouldPublishNip65)
            {
                var defaultPrefs = NostrConstants.DefaultRelays
                    .Select(url => new RelayPreference { Url = url, Usage = RelayUsage.Both })
                    .ToList();
                try
                {
                    _logger.LogInformation("Publishing default NIP-65 relay list for new key");
                    await _nostrService.PublishRelayListAsync(defaultPrefs, CurrentUser!.PrivateKeyHex);
                    await _storageService.SaveUserRelayListAsync(CurrentUser.PublicKeyHex, defaultPrefs);
                    _logger.LogInformation("Default NIP-65 relay list published successfully");
                }
                catch (Exception pubEx)
                {
                    _logger.LogWarning(pubEx, "Failed to publish default relay list");
                }
            }

            // 4. Subscribe to welcomes + group messages in parallel
            var subscriptionTasks = new List<Task>();

            if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
            {
                subscriptionTasks.Add(_nostrService.SubscribeToWelcomesAsync(
                    CurrentUser.PublicKeyHex, CurrentUser.PrivateKeyHex));
            }

            var chats = await _messageService.GetChatsAsync();
            var groupIds = chats
                .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
                .Select(c => c.NostrGroupId != null && c.NostrGroupId.Length > 0
                    ? Convert.ToHexString(c.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant())
                .ToList();

            if (groupIds.Count > 0)
            {
                var latestActivity = chats
                    .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null)
                    .Select(c => c.LastActivityAt)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                DateTimeOffset? since = latestActivity > DateTime.MinValue
                    ? new DateTimeOffset(latestActivity, TimeSpan.Zero).AddMinutes(-5)
                    : null;
                subscriptionTasks.Add(_nostrService.SubscribeToGroupMessagesAsync(groupIds, since));
            }

            // 4b. Connect bot-specific relays (receive-only, excluded from group broadcasts)
            var botRelayUrls = chats
                .Where(c => c.Type == ChatType.Bot && c.RelayUrls.Count > 0)
                .SelectMany(c => c.RelayUrls)
                .Distinct()
                .ToList();

            if (botRelayUrls.Count > 0)
            {
                _logger.LogInformation("Connecting to {Count} bot-specific relays", botRelayUrls.Count);
                subscriptionTasks.Add(_nostrService.ConnectBotRelaysAsync(botRelayUrls));
            }

            await Task.WhenAll(subscriptionTasks);

            // 5. KeyPackage check + profile metadata in parallel (non-blocking)
            var backgroundTasks = new List<Task>();

            if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
            {
                backgroundTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var myKeyPackages = await _nostrService.FetchKeyPackagesAsync(CurrentUser.PublicKeyHex);
                        if (!myKeyPackages.Any())
                            ChatListViewModel.StatusMessage = "No KeyPackage published — others cannot invite you. Publish one in Settings.";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check KeyPackage status");
                    }
                }));

                backgroundTasks.Add(Task.Run(async () =>
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
                        _logger.LogWarning(ex, "Failed to fetch own profile metadata");
                    }
                    finally
                    {
                        IsHeaderLoading = false;
                    }
                }));
            }
            else
            {
                IsHeaderLoading = false;
            }

            await Task.WhenAll(backgroundTasks);
            _logger.LogInformation("Background network initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background network initialization failed");
            IsHeaderLoading = false;
        }
    }

    private Task LogoutAsync()
    {
        _logger.LogInformation("Logout requested — delegating to ShellViewModel");
        RelayStatuses.Clear();
        ChatListViewModel.ClearChats();
        ChatViewModel.ClearChat();
        CurrentUser = null;
        IsLoggedIn = false;
        HeaderDisplayName = "OpenChat";

        _onLogoutRequested?.Invoke();
        return Task.CompletedTask;
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
