using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Microsoft.Extensions.Logging;
using Scramble.Core;
using Scramble.Core.Crypto;
using Scramble.Core.Logging;
using Scramble.Core.Models;
using Scramble.Core.Services;
using Scramble.Presentation.Services;

namespace Scramble.Presentation.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger = LoggingConfiguration.CreateLogger<MainViewModel>();
    private readonly IMessageService _messageService;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;
    private readonly IPlatformClipboard _clipboard;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private NotificationOrchestrator? _notificationOrchestrator;

    [Reactive] public partial User? CurrentUser { get; set; }
    [Reactive] public partial bool IsLoggedIn { get; set; }
    [Reactive] public partial bool IsConnected { get; set; }

    [Reactive] public partial ViewModelBase? CurrentView { get; set; }
    [Reactive] public partial string HeaderDisplayName { get; set; } = "Scramble";
    [Reactive] public partial bool IsHeaderLoading { get; set; }

    // Relay statuses
    public ObservableCollection<RelayStatusViewModel> RelayStatuses { get; } = new();
    [Reactive] public partial bool IsRelayListExpanded { get; set; }
    [Reactive] public partial int ConnectedRelayCount { get; set; }
    [Reactive] public partial int TotalRelayCount { get; set; }
    [Reactive] public partial string RelayCountText { get; set; } = "Relays: 0/0";

    // My Profile Dialog
    [Reactive] public partial bool ShowMyProfileDialog { get; set; }
    [Reactive] public partial string? MyNpub { get; set; }
    [Reactive] public partial string? MyNsec { get; set; }
    [Reactive] public partial string? CopyStatusMessage { get; set; }
    [Reactive] public partial string? MyDisplayName { get; set; }
    [Reactive] public partial string? MyName { get; set; }
    [Reactive] public partial string? MyPictureUrl { get; set; }
    [Reactive] public partial string? MyAbout { get; set; }
    [Reactive] public partial bool IsLoadingProfile { get; set; }
    [Reactive] public partial byte[]? MyNpubQrPngBytes { get; set; }

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
    public ReactiveCommand<Unit, Unit> ToggleRelayListCommand { get; }

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

        // Wire SettingsView's on-screen back arrow to navigate back to chats.
        // SettingsView is a UserControl reused across desktop and mobile shells,
        // so the back command must live on the view model rather than relying on
        // a $parent[Window] visual-tree walk (which only works on desktop).
        SettingsViewModel.BackCommand = ShowChatsCommand;

        // Wire ChatView's back arrow to clear the active chat and return to the list.
        // Only on mobile — on desktop the chat list is always visible side-by-side.
        if (launcher.IsMobile)
        {
            ChatViewModel.BackCommand = ReactiveCommand.Create(() =>
            {
                ChatViewModel.ClearChat();
                ChatListViewModel.SelectedChat = null;
            });
        }

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
            MyNpubQrPngBytes = _qrCodeGenerator.GeneratePng(BuildShareableProfilePayload(), 10);
            ShowMyProfileDialog = true;

            // Show cached profile immediately, then refresh in background
            if (!string.IsNullOrEmpty(CurrentUser?.PublicKeyHex))
            {
                IsLoadingProfile = true;
                try
                {
                    var metadata = await _messageService.GetCachedOrFetchProfileAsync(CurrentUser.PublicKeyHex);
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

        ToggleRelayListCommand = ReactiveCommand.Create(() =>
        {
            IsRelayListExpanded = !IsRelayListExpanded;
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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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
                UpdateRelayCounts();

                // Sync to SettingsViewModel relays
                var settingsRelay = SettingsViewModel.Relays.FirstOrDefault(r => r.Url == status.RelayUrl);
                if (settingsRelay != null)
                {
                    settingsRelay.IsConnected = status.IsConnected;
                    settingsRelay.Error = status.Error;
                }
            });

        // Subscribe to background metadata updates — refresh profile dialog and header if data changed
        _messageService.MetadataUpdated
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(metadata =>
            {
                // Update profile dialog if it's showing for this user
                if (ShowMyProfileDialog && CurrentUser?.PublicKeyHex == metadata.PublicKeyHex)
                {
                    MyDisplayName = metadata.DisplayName;
                    MyName = metadata.Name;
                    MyPictureUrl = metadata.Picture;
                    MyAbout = metadata.About;
                }

                // Update header display name if it's our own profile
                if (CurrentUser?.PublicKeyHex == metadata.PublicKeyHex)
                {
                    var name = metadata.GetDisplayName();
                    if (!string.IsNullOrEmpty(metadata.DisplayName) ||
                        !string.IsNullOrEmpty(metadata.Name) ||
                        !string.IsNullOrEmpty(metadata.Username))
                    {
                        HeaderDisplayName = name;
                    }
                }
            });

    }

    public async Task InitializeAfterLoginAsync()
    {
        if (CurrentUser == null) return;

        _logger.LogInformation("InitializeAfterLoginAsync starting for {Npub}...", CurrentUser.Npub?[..Math.Min(12, CurrentUser.Npub.Length)]);

        try
        {
        HeaderDisplayName = !string.IsNullOrEmpty(CurrentUser.DisplayName)
            ? CurrentUser.DisplayName
            : "Scramble";

        // Show spinners immediately while everything initializes
        ChatListViewModel.IsLoading = true;
        IsHeaderLoading = true;

        _logger.LogDebug("Initializing message service");
        await _messageService.InitializeAsync();

        // Start notification orchestrator (delegates to platform-specific INotificationService)
        _notificationOrchestrator = new NotificationOrchestrator(_messageService);
        _notificationOrchestrator.Initialize();

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
                        CurrentUser.GetSignerRelayUrls(),
                        CurrentUser.SignerRemotePubKey,
                        CurrentUser.SignerLocalPrivateKeyHex,
                        CurrentUser.SignerLocalPublicKeyHex,
                        CurrentUser.SignerSecret);

                    if (connected)
                    {
                        _logger.LogInformation("Signer session restored successfully");

                        // The signer may report a PublicKeyHex that differs from the stored
                        // CurrentUser.PublicKeyHex (stale signer state, mis-routed reply on
                        // a shared relay, foreign signer answering). Earlier this was treated
                        // as authoritative and the User row was rewritten in place — that
                        // overwrote the active profile's current_user with whatever identity
                        // the signer happened to report, including foreign accounts'. The
                        // resulting corruption was observable as "wrong npub for this profile".
                        // Identity is set authoritatively at login (HandleSignerConnectedAsync
                        // resolves the signing pubkey via get_public_key before saving the
                        // User). Don't second-guess it here — log the mismatch as a diagnostic
                        // signal and leave the saved row untouched. Use the reconcile tool to
                        // fix any data already corrupted by the previous behaviour.
                        if (ExternalSigner!.PublicKeyHex != null &&
                            !string.Equals(ExternalSigner.PublicKeyHex, CurrentUser.PublicKeyHex, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Signer reported pubkey ({SignerKey}) does not match stored user pubkey ({UserKey}) — keeping stored identity. If this persists, profile data may be split across DBs from older builds; run the reconcile tool.",
                                ExternalSigner.PublicKeyHex[..Math.Min(16, ExternalSigner.PublicKeyHex.Length)] + "...",
                                CurrentUser.PublicKeyHex[..Math.Min(16, CurrentUser.PublicKeyHex.Length)] + "...");
                        }
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
        UpdateRelayCounts();
    }

    private void UpdateRelayCounts()
    {
        ConnectedRelayCount = RelayStatuses.Count(r => r.IsConnected);
        TotalRelayCount = RelayStatuses.Count;
        RelayCountText = $"Relays: {ConnectedRelayCount}/{TotalRelayCount}";
    }

    /// <summary>
    /// Builds the payload encoded into the user's "share me" QR. Prefers a NIP-21
    /// <c>nostr:nprofile1...</c> URI carrying the pubkey plus up to 3 connected relay
    /// hints — this lets a scanning client locate the user's keypackages and metadata
    /// immediately, instead of guessing relays from a bare npub. Falls back to the bare
    /// npub if the hex pubkey isn't available (e.g. early in startup).
    ///
    /// The <c>nostr:</c> prefix lets generic QR scanners deep-link into any registered
    /// Nostr handler; Scramble's own scanner strips it on the way in.
    /// </summary>
    private string BuildShareableProfilePayload()
    {
        var hex = CurrentUser?.PublicKeyHex;
        if (string.IsNullOrEmpty(hex))
            return MyNpub ?? string.Empty;

        // Cap at 3 relays to keep the QR scannable. Prefer connected relays so the
        // hint actually works for the recipient; if none are connected (offline at
        // the moment of opening the dialog) fall back to all configured relays.
        var relays = RelayStatuses
            .Where(r => r.IsConnected)
            .Select(r => r.Url)
            .Take(3)
            .ToList();
        if (relays.Count == 0)
            relays = RelayStatuses.Select(r => r.Url).Take(3).ToList();

        try
        {
            return "nostr:" + Nip19.EncodeNprofile(hex, relays);
        }
        catch
        {
            // EncodeNprofile only throws on a malformed hex pubkey; the bare npub is
            // still useful even without relay hints.
            return MyNpub ?? string.Empty;
        }
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
                var defaultUrls = NostrConstants.DefaultRelays.ToList();
                try
                {
                    _logger.LogInformation("Publishing default relay lists (kind 10002, 10050, 10051) for new key");
                    await _nostrService.PublishRelayListAsync(defaultPrefs, CurrentUser!.PrivateKeyHex);
                    await _nostrService.PublishDmRelayListAsync(defaultUrls, CurrentUser.PrivateKeyHex);
                    await _nostrService.PublishKeyPackageRelayListAsync(defaultUrls, CurrentUser.PrivateKeyHex);
                    await _storageService.SaveUserRelayListAsync(CurrentUser.PublicKeyHex, defaultPrefs);
                    _logger.LogInformation("Default relay lists published successfully");
                }
                catch (Exception pubEx)
                {
                    _logger.LogWarning(pubEx, "Failed to publish default relay lists");
                }
            }

            // 4. Connect additional relays + group subscriptions in parallel
            var subscriptionTasks = new List<Task>();

            var chats = await _messageService.GetChatsAsync();

            // Include archived chats in relay subscriptions so messages are still received
            var archivedChats = await _storageService.GetArchivedChatsAsync();
            var allGroupChats = chats.Concat(archivedChats);

            var groupIds = allGroupChats
                .Where(c => c.Type == ChatType.Group && c.MlsGroupId != null && c.MlsGroupId.Length > 0)
                .Select(c => c.NostrGroupId != null && c.NostrGroupId.Length > 0
                    ? Convert.ToHexString(c.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant())
                .Distinct()
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

            // 4b. Ensure group chat relays are connected (normal relays, not bot-only)
            var groupRelayUrls = chats
                .Where(c => c.Type == ChatType.Group && c.RelayUrls.Count > 0)
                .SelectMany(c => c.RelayUrls)
                .Distinct()
                .Where(url => !_nostrService.ConnectedRelayUrls.Any(c =>
                    string.Equals(c.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (groupRelayUrls.Count > 0)
            {
                _logger.LogInformation("Connecting to {Count} missing group chat relay(s): {Relays}",
                    groupRelayUrls.Count, string.Join(", ", groupRelayUrls));
                subscriptionTasks.Add(_nostrService.ConnectAsync(groupRelayUrls));
            }

            // 4c. Connect bot-specific relays (receive-only, excluded from group broadcasts)
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

            // 4d. Connect outbox relays (contacts' preferred relays) for gift wrap delivery/reception
            try
            {
                var contacts = await _storageService.GetContactsAsync(CurrentUser.PublicKeyHex);
                if (contacts.Count > 0)
                {
                    // Collect cached relay URLs for each contact, aggregate by frequency
                    var relayFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var contact in contacts)
                    {
                        // Check cached DM relays (kind 10050) first, then NIP-65
                        var dmCached = await _storageService.GetContactRelayListAsync(contact.PublicKeyHex, 10050);
                        if (dmCached.HasValue && dmCached.Value.Urls.Count > 0)
                        {
                            foreach (var url in dmCached.Value.Urls)
                            {
                                var normalized = url.TrimEnd('/');
                                relayFrequency[normalized] = relayFrequency.GetValueOrDefault(normalized) + 1;
                            }
                        }
                        else
                        {
                            var nip65Cached = await _storageService.GetContactRelayPreferencesAsync(contact.PublicKeyHex);
                            if (nip65Cached.HasValue)
                            {
                                foreach (var pref in nip65Cached.Value.Relays.Where(r =>
                                    r.Usage == RelayUsage.Read || r.Usage == RelayUsage.Both))
                                {
                                    var normalized = pref.Url.TrimEnd('/');
                                    relayFrequency[normalized] = relayFrequency.GetValueOrDefault(normalized) + 1;
                                }
                            }
                        }
                    }

                    if (relayFrequency.Count > 0)
                    {
                        // Take top 12 most common relays, excluding ones we're already connected to
                        var topOutboxRelays = relayFrequency
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .Where(url => !_nostrService.ConnectedRelayUrls.Any(c =>
                                string.Equals(c.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                            .Take(12)
                            .ToList();

                        if (topOutboxRelays.Count > 0)
                        {
                            _logger.LogInformation(
                                "Connecting to {Count} outbox relays from {ContactCount} contacts' cached relay lists",
                                topOutboxRelays.Count, contacts.Count);
                            subscriptionTasks.Add(_nostrService.ConnectOutboxRelaysAsync(topOutboxRelays));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect outbox relays for contacts");
            }

            await Task.WhenAll(subscriptionTasks);

            // 4e. Subscribe to welcomes/gift wraps on ALL connected relays (including outbox)
            if (!string.IsNullOrEmpty(CurrentUser.PublicKeyHex))
            {
                await _nostrService.SubscribeToWelcomesAsync(
                    CurrentUser.PublicKeyHex, CurrentUser.PrivateKeyHex);
            }

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
                        {
                            // No KeyPackage on relays — auto-publish one so others can invite this user
                            // immediately (covers freshly-created identities and recovered keys with no
                            // prior publish). Idempotent: if a local unused KP already exists, no-op.
                            _logger.LogInformation("No KeyPackage found on relays — auto-publishing one");
                            await _messageService.AutoPublishKeyPackageIfNeededAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check or auto-publish KeyPackage");
                    }
                }));

                // Restore NIP-17 bot/agent chats from relay history. The live kind 1059
                // subscription should also pick these up, but explicit fetch ensures we
                // don't miss anything on a fresh device or after a profile reset, and
                // chats land in the Agents (DVM) tab without the user re-adding devices.
                backgroundTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await _messageService.RescanNip17ChatsAsync();
                        // Refresh the chat list so newly-created bot chats appear in the UI
                        await ChatListViewModel.LoadChatsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rescan NIP-17 chats");
                    }
                }));

                backgroundTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Show cached profile immediately; background relay refresh
                        // will emit MetadataUpdated if anything changed (handled by
                        // the subscription in the constructor).
                        var metadata = await _messageService.GetCachedOrFetchProfileAsync(CurrentUser.PublicKeyHex);
                        if (metadata != null)
                        {
                            MyDisplayName = metadata.DisplayName;
                            MyName = metadata.Name;
                            MyPictureUrl = metadata.Picture;
                            MyAbout = metadata.About;
                            // Only update header if metadata has a real name — don't downgrade
                            // from a cached display name to a truncated npub
                            var fetchedName = metadata.GetDisplayName();
                            if (!string.IsNullOrEmpty(metadata.DisplayName) ||
                                !string.IsNullOrEmpty(metadata.Name) ||
                                !string.IsNullOrEmpty(metadata.Username))
                            {
                                HeaderDisplayName = fetchedName;
                                // Persist real name back to CurrentUser and DB so restarts don't
                                // show the placeholder until metadata is fetched again
                                CurrentUser.DisplayName = fetchedName;
                                SettingsViewModel.DisplayName = fetchedName;
                                try
                                {
                                    await _storageService.SaveCurrentUserAsync(CurrentUser);
                                    _logger.LogInformation("Persisted fetched display name '{Name}' for current user", fetchedName);
                                }
                                catch (Exception saveEx)
                                {
                                    _logger.LogWarning(saveEx, "Failed to persist fetched display name");
                                }
                            }
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
        _notificationOrchestrator?.Dispose();
        _notificationOrchestrator = null;
        RelayStatuses.Clear();
        ChatListViewModel.ClearChats();
        ChatViewModel.ClearChat();
        CurrentUser = null;
        IsLoggedIn = false;
        HeaderDisplayName = "Scramble";

        _onLogoutRequested?.Invoke();
        return Task.CompletedTask;
    }
}

public partial class RelayStatusViewModel : ViewModelBase
{
    [Reactive] public partial string Url { get; set; } = string.Empty;
    [Reactive] public partial bool IsConnected { get; set; }
    [Reactive] public partial string? Error { get; set; }
    [Reactive] public partial bool IsReconnecting { get; set; }

    /// <summary>Short display name: strips wss:// and trailing /</summary>
    public string DisplayName => Url.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
}
