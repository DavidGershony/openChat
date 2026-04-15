using System.Collections.ObjectModel;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core.Crypto;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.Presentation.ViewModels;

public class ChatListViewModel : ViewModelBase
{
    private readonly ILogger<ChatListViewModel> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly IMlsService? _mlsService;
    private readonly INostrService? _nostrService;
    private string? _currentUserPubKeyHex;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private IDisposable? _chatUpdateSubscription;
    private IDisposable? _inviteSubscription;
    private IDisposable? _decryptionErrorSubscription;
    private IDisposable? _skippedInviteSubscription;

    public ObservableCollection<ChatItemViewModel> Chats { get; } = new();
    public ObservableCollection<ChatItemViewModel> ArchivedChats { get; } = new();
    public ObservableCollection<PendingInviteItemViewModel> PendingInvites { get; } = new();
    public ObservableCollection<FollowContactViewModel> Following { get; } = new();
    [Reactive] public bool IsRefreshingFollowing { get; set; }

    [Reactive] public ChatItemViewModel? SelectedChat { get; set; }
    [Reactive] public int ArchivedChatsCount { get; set; }
    [Reactive] public bool ShowArchivedSection { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public int PendingInviteCount { get; set; }
    [Reactive] public int SkippedInviteCount { get; set; }
    [Reactive] public string? StatusMessage { get; set; }

    // New Chat Dialog
    [Reactive] public bool ShowNewChatDialog { get; set; }
    [Reactive] public string NewChatPublicKey { get; set; } = string.Empty;
    [Reactive] public string NewChatName { get; set; } = string.Empty;
    [Reactive] public string? NewChatError { get; set; }
    [Reactive] public bool IsNpubInvalid { get; set; }
    [Reactive] public bool IsLookingUpKeyPackage { get; set; }
    [Reactive] public string? KeyPackageStatus { get; set; }
    [Reactive] public bool HasKeyPackage { get; set; }
    [Reactive] public bool HasMultipleKeyPackages { get; set; }
    [Reactive] public DateTime? KeyPackageCreatedAt { get; set; }
    [Reactive] public string? KeyPackageRelays { get; set; }
    [Reactive] public string? CreateProgress { get; set; }
    public ObservableCollection<KeyPackageItemViewModel> FoundKeyPackages { get; } = new();
    [Reactive] public KeyPackageItemViewModel? SelectedKeyPackage { get; set; }
    private IDisposable? _autoLookupSubscription;
    private IDisposable? _autoGroupLookupSubscription;

    // New Group Dialog
    [Reactive] public bool ShowNewGroupDialog { get; set; }
    [Reactive] public string NewGroupName { get; set; } = string.Empty;
    [Reactive] public string NewGroupDescription { get; set; } = string.Empty;
    [Reactive] public string NewGroupMembers { get; set; } = string.Empty;
    [Reactive] public string? NewGroupError { get; set; }
    [Reactive] public bool IsLookingUpGroupKeyPackages { get; set; }
    [Reactive] public string? GroupKeyPackageStatus { get; set; }

    // Relay Selection (shared by both dialogs)
    public ObservableCollection<RelaySelectionItemViewModel> SelectableRelays { get; } = new();
    [Reactive] public int SelectedRelayCount { get; set; }

    // Join Group Dialog
    [Reactive] public bool ShowJoinGroupDialog { get; set; }
    [Reactive] public string JoinGroupId { get; set; } = string.Empty;
    [Reactive] public string? JoinGroupError { get; set; }

    // Add Bot / DM Dialog
    [Reactive] public bool ShowAddBotDialog { get; set; }
    [Reactive] public string BotNpub { get; set; } = string.Empty;
    [Reactive] public string? AddBotError { get; set; }

    // Relay selection for bot chat
    [Reactive] public bool BotRelayModeNip65 { get; set; } = true;
    [Reactive] public bool BotRelayModeList { get; set; }
    [Reactive] public bool BotRelayModeManual { get; set; }
    [Reactive] public string BotManualRelay { get; set; } = string.Empty;
    [Reactive] public bool IsFetchingNip65 { get; set; }
    [Reactive] public string? Nip65Status { get; set; }
    public ObservableCollection<RelayCheckItem> BotAvailableRelays { get; } = new();
    public ObservableCollection<RelayCheckItem> BotNip65Relays { get; } = new();

    // Delete Chat Dialog
    [Reactive] public bool ShowDeleteChatDialog { get; set; }
    [Reactive] public ChatItemViewModel? ChatToDelete { get; set; }

    // Reset Group Dialog
    [Reactive] public bool ShowResetGroupDialog { get; set; }
    [Reactive] public ChatItemViewModel? GroupToReset { get; set; }

    // Rename Chat Dialog
    [Reactive] public bool ShowRenameChatDialog { get; set; }
    [Reactive] public ChatItemViewModel? ChatToRename { get; set; }
    [Reactive] public string RenameChatInput { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> NewChatCommand { get; }
    public ReactiveCommand<Unit, Unit> NewGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> JoinGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelNewChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelNewGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmJoinGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelJoinGroupCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> DeleteChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDeleteChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ResetGroupCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> RenameChatCommand { get; }
    public ReactiveCommand<FollowContactViewModel, Unit> SelectFollowCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshFollowingCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmRenameChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRenameChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ArchiveChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> UnarchiveChatCommand { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleArchivedViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupKeyPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupGroupKeyPackagesCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissSkippedInviteNoticeCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> DeclineInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanInvitesCommand { get; }
    [Reactive] public bool IsRescanningInvites { get; set; }
    public ReactiveCommand<Unit, Unit> AddBotCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateBotChatCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddBotCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupBotNip65Command { get; }

    public ChatListViewModel(IMessageService messageService, IStorageService storageService, IMlsService? mlsService = null, INostrService? nostrService = null)
    {
        _logger = LoggingConfiguration.CreateLogger<ChatListViewModel>();
        _messageService = messageService;
        _storageService = storageService;
        _mlsService = mlsService;
        _nostrService = nostrService;

        NewChatCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening new chat dialog");
            NewChatPublicKey = string.Empty;
            NewChatName = string.Empty;
            NewChatError = null;
            IsNpubInvalid = false;
            KeyPackageStatus = null;
            HasKeyPackage = false;
            KeyPackageCreatedAt = null;
            KeyPackageRelays = null;
            FoundKeyPackages.Clear();
            SelectedKeyPackage = null;
            HasMultipleKeyPackages = false;
            CreateProgress = null;
            PopulateSelectableRelays();
            ShowNewChatDialog = true;
        });

        NewGroupCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening new group dialog");
            NewGroupName = string.Empty;
            NewGroupDescription = string.Empty;
            NewGroupMembers = string.Empty;
            NewGroupError = null;
            PopulateSelectableRelays();
            ShowNewGroupDialog = true;
        });

        JoinGroupCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening join group dialog");
            JoinGroupId = string.Empty;
            JoinGroupError = null;
            ShowJoinGroupDialog = true;
        });

        var canCreateGroup = this.WhenAnyValue(
            x => x.NewGroupName,
            x => x.SelectedRelayCount,
            (name, relayCount) => !string.IsNullOrWhiteSpace(name) && name.Length >= 1 && relayCount > 0);

        CreateGroupCommand = ReactiveCommand.CreateFromTask(CreateNewGroupAsync, canCreateGroup);

        CancelNewGroupCommand = ReactiveCommand.Create(() =>
        {
            ShowNewGroupDialog = false;
            NewGroupError = null;
        });

        var canJoinGroup = this.WhenAnyValue(
            x => x.JoinGroupId,
            id => !string.IsNullOrWhiteSpace(id) && id.Length >= 8);

        ConfirmJoinGroupCommand = ReactiveCommand.CreateFromTask(JoinGroupAsync, canJoinGroup);

        CancelJoinGroupCommand = ReactiveCommand.Create(() =>
        {
            ShowJoinGroupDialog = false;
            JoinGroupError = null;
        });

        AddBotCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening link device dialog");
            BotNpub = string.Empty;
            AddBotError = null;
            BotRelayModeNip65 = true;
            BotRelayModeList = false;
            BotRelayModeManual = false;
            BotManualRelay = string.Empty;
            Nip65Status = null;
            IsFetchingNip65 = false;
            BotNip65Relays.Clear();
            BotAvailableRelays.Clear();
            if (_nostrService != null)
            {
                foreach (var url in _nostrService.ConnectedRelayUrls)
                    BotAvailableRelays.Add(new RelayCheckItem(url, true));
            }
            ShowAddBotDialog = true;
        });

        var canCreateBot = this.WhenAnyValue(
            x => x.BotNpub,
            npub => !string.IsNullOrWhiteSpace(npub) && npub.Trim().Length > 5);

        CreateBotChatCommand = ReactiveCommand.CreateFromTask(CreateBotChatAsync, canCreateBot);

        CancelAddBotCommand = ReactiveCommand.Create(() =>
        {
            ShowAddBotDialog = false;
            AddBotError = null;
        });

        LookupBotNip65Command = ReactiveCommand.CreateFromTask(LookupBotNip65Async);

        DeleteChatCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Requesting delete for chat: {ChatId} - {ChatName}", chat.Id, chat.Name);
            ChatToDelete = chat;
            ShowDeleteChatDialog = true;
        });

        ConfirmDeleteChatCommand = ReactiveCommand.CreateFromTask(DeleteChatAsync);

        CancelDeleteChatCommand = ReactiveCommand.Create(() =>
        {
            ShowDeleteChatDialog = false;
            ChatToDelete = null;
        });

        ResetGroupCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Requesting reset for group: {ChatId} - {ChatName}", chat.Id, chat.Name);
            GroupToReset = chat;
            ShowResetGroupDialog = true;
        });

        ConfirmResetGroupCommand = ReactiveCommand.CreateFromTask(ResetGroupAsync);

        CancelResetGroupCommand = ReactiveCommand.Create(() =>
        {
            ShowResetGroupDialog = false;
            GroupToReset = null;
        });

        RenameChatCommand = ReactiveCommand.Create<ChatItemViewModel>(chat =>
        {
            _logger.LogInformation("Opening rename dialog for chat: {ChatId} - {ChatName}", chat.Id, chat.Name);
            ChatToRename = chat;
            RenameChatInput = chat.Name;
            ShowRenameChatDialog = true;
        });

        ConfirmRenameChatCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var chat = ChatToRename;
            var newName = RenameChatInput?.Trim() ?? string.Empty;
            if (chat != null && !string.IsNullOrEmpty(newName) && newName != chat.Name)
            {
                await RenameChatAsync(chat, newName);
            }
            ShowRenameChatDialog = false;
            ChatToRename = null;
            RenameChatInput = string.Empty;
        });

        CancelRenameChatCommand = ReactiveCommand.Create(() =>
        {
            ShowRenameChatDialog = false;
            ChatToRename = null;
            RenameChatInput = string.Empty;
        });

        SelectFollowCommand = ReactiveCommand.Create<FollowContactViewModel>(contact =>
        {
            NewChatPublicKey = contact.Npub;
        });

        // Filter Following list as the user types in the npub / search box
        this.WhenAnyValue(x => x.NewChatPublicKey)
            .Throttle(TimeSpan.FromMilliseconds(120))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q =>
            {
                var query = (q ?? string.Empty).Trim();
                // If full npub typed, skip filtering (user already has a full key)
                bool isFullNpub = query.StartsWith("npub1") && query.Length >= 60;
                foreach (var f in Following)
                {
                    if (isFullNpub)
                    {
                        f.IsVisible = string.Equals(f.Npub, query, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.IsNullOrEmpty(query))
                    {
                        f.IsVisible = true;
                    }
                    else
                    {
                        f.IsVisible = (f.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (f.Petname?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                            || f.Npub.Contains(query, StringComparison.OrdinalIgnoreCase);
                    }
                }
            });

        RefreshFollowingCommand = ReactiveCommand.CreateFromTask(async () => await RefreshFollowingAsync());

        ArchiveChatCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            _logger.LogInformation("Archiving chat: {ChatId} - {Name}", chat.Id, chat.Name);
            await _messageService.ArchiveChatAsync(chat.Id);
            Chats.Remove(chat);
            ArchivedChats.Add(chat);
            ArchivedChatsCount = ArchivedChats.Count;
        });

        UnarchiveChatCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            _logger.LogInformation("Unarchiving chat: {ChatId} - {Name}", chat.Id, chat.Name);
            await _messageService.UnarchiveChatAsync(chat.Id);
            ArchivedChats.Remove(chat);
            Chats.Add(chat);
            ArchivedChatsCount = ArchivedChats.Count;
        });

        ToggleArchivedViewCommand = ReactiveCommand.Create(() =>
        {
            ShowArchivedSection = !ShowArchivedSection;
        });

        ToggleMuteCommand = ReactiveCommand.CreateFromTask<ChatItemViewModel>(async chat =>
        {
            var newMuted = !chat.IsMuted;
            _logger.LogInformation("{Action} chat: {ChatId} - {Name}",
                newMuted ? "Muting" : "Unmuting", chat.Id, chat.Name);
            if (newMuted)
                await _messageService.MuteChatAsync(chat.Id);
            else
                await _messageService.UnmuteChatAsync(chat.Id);
            chat.IsMuted = newMuted;
        });

        var canLookupKeyPackage = this.WhenAnyValue(
            x => x.NewChatPublicKey,
            x => x.IsLookingUpKeyPackage,
            (key, looking) => !string.IsNullOrWhiteSpace(key) && key.Length >= 8 && !looking);

        LookupKeyPackageCommand = ReactiveCommand.CreateFromTask(LookupKeyPackageAsync, canLookupKeyPackage);

        // Auto-trigger KeyPackage lookup when a valid npub is entered
        _autoLookupSubscription = this.WhenAnyValue(x => x.NewChatPublicKey)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(key =>
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length < 63)
                {
                    // Not long enough yet — no validation, no lookup
                    IsNpubInvalid = false;
                    return;
                }

                // Length is right — validate format
                if (!key.StartsWith("npub1") || key.Trim().Length != 63)
                {
                    IsNpubInvalid = true;
                    NewChatError = null;
                    return;
                }

                // Check self-invite
                var hex = _nostrService?.NpubToHex(key.Trim());
                if (hex != null && _currentUserPubKeyHex != null &&
                    hex.Equals(_currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase))
                {
                    IsNpubInvalid = true;
                    NewChatError = "You can't invite yourself";
                    _logger.LogWarning("Auto-lookup: user attempted to invite themselves");
                    return;
                }

                // Valid npub — trigger lookup immediately
                IsNpubInvalid = false;
                NewChatError = null;
                if (!IsLookingUpKeyPackage)
                {
                    LookupKeyPackageCommand.Execute().Subscribe();
                }
            });

        var canLookupGroupKeyPackages = this.WhenAnyValue(
            x => x.NewGroupMembers,
            x => x.IsLookingUpGroupKeyPackages,
            (members, looking) => !string.IsNullOrWhiteSpace(members) && !looking);

        LookupGroupKeyPackagesCommand = ReactiveCommand.CreateFromTask(LookupGroupKeyPackagesAsync, canLookupGroupKeyPackages);

        // Auto-trigger group KeyPackage lookup when members field contains valid npubs
        _autoGroupLookupSubscription = this.WhenAnyValue(x => x.NewGroupMembers)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(members => !string.IsNullOrWhiteSpace(members) && members.Contains("npub1") && members.Length >= 63)
            .Subscribe(_ =>
            {
                if (!IsLookingUpGroupKeyPackages)
                {
                    LookupGroupKeyPackagesCommand.Execute().Subscribe();
                }
            });

        AcceptInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(AcceptInviteAsync);
        DeclineInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(DeclineInviteAsync);

        var canRescan = this.WhenAnyValue(x => x.IsRescanningInvites, scanning => !scanning);
        RescanInvitesCommand = ReactiveCommand.CreateFromTask(RescanInvitesAsync, canRescan);

        var canCreateChat = this.WhenAnyValue(
                x => x.SelectedKeyPackage,
                x => x.SelectedRelayCount)
            .Select(t => t.Item1 != null && t.Item2 > 0);

        CreateChatCommand = ReactiveCommand.CreateFromTask(CreateNewChatAsync, canCreateChat);

        CancelNewChatCommand = ReactiveCommand.Create(() =>
        {
            ShowNewChatDialog = false;
            NewChatError = null;
            CreateProgress = null;
        });

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadChatsAsync);

        // Subscribe to chat updates
        _chatUpdateSubscription = _messageService.ChatUpdates
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnChatUpdated);

        // Subscribe to new invites
        _inviteSubscription = _messageService.NewInvites
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnNewInvite);

        // Subscribe to MLS decryption errors
        _decryptionErrorSubscription = _messageService.DecryptionErrors
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                StatusMessage = $"Failed to decrypt message in \"{error.ChatName}\". Group may need reset.";
            });

        // Subscribe to skipped invite notifications
        _skippedInviteSubscription = _messageService.SkippedInvites
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SkippedInviteCount++);

        DismissSkippedInviteNoticeCommand = ReactiveCommand.CreateFromTask(DismissSkippedInviteNoticeAsync);

        // Filter chats based on search
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter());
    }

    public async Task LoadChatsAsync()
    {
        IsLoading = true;

        try
        {
            // Cache current user pubkey for self-invite validation
            var currentUser = await _storageService.GetCurrentUserAsync();
            _currentUserPubKeyHex = currentUser?.PublicKeyHex;
            _logger.LogInformation("LoadChats: currentUserPubKey={PubKey}",
                _currentUserPubKeyHex?[..Math.Min(16, _currentUserPubKeyHex?.Length ?? 0)] ?? "null");

            var chats = await _messageService.GetChatsAsync();
            _logger.LogInformation("LoadChats: {Total} chats loaded from DB (IsArchived=0)", chats.Count());

            Chats.Clear();
            foreach (var chat in chats
                .OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastActivityAt))
            {
                var isParticipant = _currentUserPubKeyHex == null ||
                    chat.ParticipantPublicKeys.Any(p => string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));

                if (!isParticipant)
                {
                    _logger.LogWarning("LoadChats: chat {ChatId} '{ChatName}' (type={Type}) has {Count} participants but current user NOT found. Participants: [{Participants}]. Marking as orphaned.",
                        chat.Id[..Math.Min(8, chat.Id.Length)], chat.Name, chat.Type,
                        chat.ParticipantPublicKeys.Count,
                        string.Join(", ", chat.ParticipantPublicKeys.Select(p => p[..Math.Min(16, p.Length)])));
                }

                var chatItem = new ChatItemViewModel(chat);
                chatItem.NeedsRepair = !isParticipant;

                // Resolve avatar: other participant (DM/bot), or group image / creator / admin (group)
                if (_currentUserPubKeyHex != null)
                {
                    var resolved = await ResolveCachedAvatarAsync(chat);
                    if (resolved != null) chatItem.LocalAvatarPath = resolved;
                }

                Chats.Add(chatItem);
            }

            // Load archived chats
            var archivedChats = await _storageService.GetArchivedChatsAsync();
            ArchivedChats.Clear();
            foreach (var chat in archivedChats.OrderByDescending(c => c.LastActivityAt))
            {
                ArchivedChats.Add(new ChatItemViewModel(chat));
            }
            ArchivedChatsCount = ArchivedChats.Count;
            _logger.LogInformation("LoadChats: {Count} archived chats loaded", ArchivedChatsCount);

            // Load pending invites
            var invites = await _messageService.GetPendingInvitesAsync();
            PendingInvites.Clear();
            foreach (var invite in invites)
            {
                PendingInvites.Add(new PendingInviteItemViewModel(invite));
            }
            PendingInviteCount = PendingInvites.Count;
            SkippedInviteCount = await _storageService.GetSkippedInviteCountAsync();

            // Kick off background avatar refresh for DM chats missing profile images
            _ = Task.Run(() => RefreshAvatarsAsync());

            // Seed contacts table from every participant we know about (DM partners + group members)
            if (_currentUserPubKeyHex != null)
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var contacts = new List<Contact>();
                    foreach (var chat in chats.Concat(archivedChats))
                    {
                        foreach (var pk in chat.ParticipantPublicKeys)
                        {
                            if (string.IsNullOrEmpty(pk)) continue;
                            if (string.Equals(pk, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!seen.Add(pk)) continue;
                            contacts.Add(new Contact
                            {
                                PublicKeyHex = pk,
                                Source = "group",
                                LastInteractedAt = chat.LastActivityAt
                            });
                        }
                    }
                    if (contacts.Count > 0)
                        await _storageService.UpsertContactsAsync(_currentUserPubKeyHex, contacts);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to seed contacts from chats");
                }
            }

            // Load cached follows + refresh from relays in background
            await LoadFollowingFromCacheAsync();
            _ = Task.Run(() => RefreshFollowingAsync());
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string AvatarCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenChat", "avatars");

    private static string? FindGroupAvatarCachePath(string chatId)
    {
        if (!Directory.Exists(AvatarCacheDir)) return null;
        var prefix = $"group_{chatId[..Math.Min(16, chatId.Length)]}.";
        return Directory.EnumerateFiles(AvatarCacheDir)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveCachedAvatarAsync(Chat chat)
    {
        if (chat.Type != ChatType.Group)
        {
            var otherPubKey = chat.ParticipantPublicKeys.FirstOrDefault(
                p => !string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));
            if (otherPubKey == null) return null;
            var otherUser = await _storageService.GetUserByPublicKeyAsync(otherPubKey);
            return otherUser?.LocalAvatarPath is { } p && File.Exists(p) ? p : null;
        }

        var groupPath = FindGroupAvatarCachePath(chat.Id);
        if (groupPath != null) return groupPath;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(chat.CreatorPublicKey)) candidates.Add(chat.CreatorPublicKey);
        foreach (var a in chat.AdminPublicKeys)
            if (!candidates.Contains(a, StringComparer.OrdinalIgnoreCase)) candidates.Add(a);

        foreach (var key in candidates)
        {
            var user = await _storageService.GetUserByPublicKeyAsync(key);
            if (user?.LocalAvatarPath is { } p && File.Exists(p)) return p;
        }
        return null;
    }

    private async Task<string?> DownloadAvatarToCacheAsync(string imageUrl, string cacheKey)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var avatarUri) ||
            (avatarUri.Scheme != "https" && avatarUri.Scheme != "http"))
            return null;
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(avatarUri.Host);
            foreach (var addr in addresses)
            {
                if (System.Net.IPAddress.IsLoopback(addr)) return null;
                var b = addr.GetAddressBytes();
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) || b[0] == 127 ||
                     (b[0] == 169 && b[1] == 254) || b[0] == 0))
                    return null;
            }
        }
        catch { return null; }

        var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
        if (imageBytes.Length > 2 * 1024 * 1024)
        {
            _logger.LogWarning("Avatar too large for {Key}: {Size} bytes", cacheKey, imageBytes.Length);
            return null;
        }

        var ext = ".jpg";
        var urlPath = avatarUri.AbsolutePath;
        if (urlPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ext = ".png";
        else if (urlPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) ext = ".webp";
        else if (urlPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) ext = ".gif";

        Directory.CreateDirectory(AvatarCacheDir);
        var localPath = Path.Combine(AvatarCacheDir, $"{cacheKey}{ext}");
        await File.WriteAllBytesAsync(localPath, imageBytes);
        return localPath;
    }

    private async Task RefreshAvatarsAsync()
    {
        if (_nostrService == null || _currentUserPubKeyHex == null) return;

        Directory.CreateDirectory(AvatarCacheDir);

        await RefreshGroupAvatarsAsync();

        // Collect DM/bot chats that need avatar refresh
        var chatsToRefresh = Chats
            .Where(c => !c.IsGroup && string.IsNullOrEmpty(c.LocalAvatarPath))
            .ToList();

        foreach (var chatItem in chatsToRefresh)
        {
            try
            {
                var otherPubKey = chatItem.Chat.ParticipantPublicKeys
                    .FirstOrDefault(p => p != _currentUserPubKeyHex);
                if (otherPubKey == null) continue;

                // Check if user already has a cached avatar we missed
                var existingUser = await _storageService.GetUserByPublicKeyAsync(otherPubKey);
                if (existingUser?.LocalAvatarPath != null && File.Exists(existingUser.LocalAvatarPath))
                {
                    RxApp.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = existingUser.LocalAvatarPath);
                    continue;
                }

                // Fetch kind 0 metadata from relays
                var metadata = await _nostrService.FetchUserMetadataAsync(otherPubKey);
                if (string.IsNullOrEmpty(metadata?.Picture)) continue;

                var localPath = await DownloadAvatarToCacheAsync(metadata.Picture, otherPubKey[..16]);
                if (localPath == null) continue;

                // Update user record in DB
                if (existingUser != null)
                {
                    existingUser.AvatarUrl = metadata.Picture;
                    existingUser.LocalAvatarPath = localPath;
                    existingUser.DisplayName = metadata.GetDisplayName();
                    existingUser.LastUpdatedAt = DateTime.UtcNow;
                    await _storageService.SaveUserAsync(existingUser);
                }

                // Update the chat item on the UI thread
                RxApp.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);

                _logger.LogInformation("Avatar cached for {PubKey}: {Path}", otherPubKey[..16], localPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh avatar for chat {ChatId}", chatItem.Id[..Math.Min(8, chatItem.Id.Length)]);
            }
        }
    }

    private async Task LoadFollowingFromCacheAsync()
    {
        if (_currentUserPubKeyHex == null) return;

        try
        {
            var cached = await _storageService.GetContactsAsync(_currentUserPubKeyHex);
            Following.Clear();
            foreach (var c in cached)
            {
                var user = await _storageService.GetUserByPublicKeyAsync(c.PublicKeyHex);
                var npub = user?.Npub ?? Bech32.Encode("npub", Convert.FromHexString(c.PublicKeyHex));
                Following.Add(new FollowContactViewModel(
                    c.PublicKeyHex, npub, c.Petname,
                    user?.DisplayName,
                    user?.LocalAvatarPath is { } p && File.Exists(p) ? p : null));
            }
            _logger.LogInformation("Loaded {Count} cached contacts", Following.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached follows");
        }
    }

    private async Task RefreshFollowingAsync()
    {
        if (_nostrService == null || _currentUserPubKeyHex == null) return;

        IsRefreshingFollowing = true;
        try
        {
            var follows = await _nostrService.FetchFollowingListAsync(_currentUserPubKeyHex);
            if (follows.Count == 0)
            {
                _logger.LogInformation("No remote follow list found");
                return;
            }

            await _storageService.SaveFollowsAsync(_currentUserPubKeyHex, follows);

            // Rebuild UI list preserving existing VMs where possible
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                var existing = Following.ToDictionary(f => f.PublicKeyHex, StringComparer.OrdinalIgnoreCase);
                Following.Clear();
                foreach (var f in follows)
                {
                    if (existing.TryGetValue(f.PublicKeyHex, out var vm))
                    {
                        vm.Petname = f.Petname ?? vm.Petname;
                        Following.Add(vm);
                    }
                    else
                    {
                        var npub = Bech32.Encode("npub", Convert.FromHexString(f.PublicKeyHex));
                        Following.Add(new FollowContactViewModel(f.PublicKeyHex, npub, f.Petname, null, null));
                    }
                }
            });

            _ = Task.Run(() => RefreshFollowingMetadataAsync(follows.Select(f => f.PublicKeyHex).ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh following list");
        }
        finally
        {
            IsRefreshingFollowing = false;
        }
    }

    private async Task RefreshFollowingMetadataAsync(List<string> pubKeys)
    {
        if (_nostrService == null) return;

        Directory.CreateDirectory(AvatarCacheDir);

        // Limit concurrency so we don't hammer relays
        using var gate = new SemaphoreSlim(4);
        var tasks = pubKeys.Select(async pubKey =>
        {
            await gate.WaitAsync();
            try
            {
                var existing = await _storageService.GetUserByPublicKeyAsync(pubKey);
                UserMetadata? metadata = null;

                if (existing == null || string.IsNullOrEmpty(existing.DisplayName))
                {
                    metadata = await _nostrService.FetchUserMetadataAsync(pubKey);
                }

                string? localPath = existing?.LocalAvatarPath is { } p && File.Exists(p) ? p : null;
                if (localPath == null && !string.IsNullOrEmpty(metadata?.Picture))
                {
                    localPath = await DownloadAvatarToCacheAsync(metadata.Picture, pubKey[..16]);
                }

                if (metadata != null || localPath != null)
                {
                    var user = existing ?? new User
                    {
                        Id = pubKey,
                        PublicKeyHex = pubKey,
                        Npub = Bech32.Encode("npub", Convert.FromHexString(pubKey)),
                        CreatedAt = DateTime.UtcNow
                    };
                    if (metadata != null)
                    {
                        user.DisplayName = metadata.GetDisplayName();
                        user.AvatarUrl = metadata.Picture ?? user.AvatarUrl;
                    }
                    if (localPath != null) user.LocalAvatarPath = localPath;
                    user.LastUpdatedAt = DateTime.UtcNow;
                    await _storageService.SaveUserAsync(user);

                    RxApp.MainThreadScheduler.Schedule(() =>
                    {
                        var vm = Following.FirstOrDefault(f => string.Equals(f.PublicKeyHex, pubKey, StringComparison.OrdinalIgnoreCase));
                        if (vm != null)
                        {
                            if (metadata != null) vm.DisplayName = metadata.GetDisplayName();
                            if (localPath != null) vm.LocalAvatarPath = localPath;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metadata refresh failed for {Pub}", pubKey[..16]);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        _logger.LogInformation("Background metadata refresh complete for {Count} follows", pubKeys.Count);
    }

    private async Task RefreshGroupAvatarsAsync()
    {
        if (_nostrService == null) return;

        var groupsToRefresh = Chats
            .Where(c => c.IsGroup && string.IsNullOrEmpty(c.LocalAvatarPath))
            .ToList();

        foreach (var chatItem in groupsToRefresh)
        {
            try
            {
                var chat = chatItem.Chat;
                var cacheKey = $"group_{chat.Id[..Math.Min(16, chat.Id.Length)]}";

                // 1. Group image set on the chat itself
                if (!string.IsNullOrEmpty(chat.AvatarUrl))
                {
                    var localPath = await DownloadAvatarToCacheAsync(chat.AvatarUrl, cacheKey);
                    if (localPath != null)
                    {
                        RxApp.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);
                        continue;
                    }
                }

                // 2. Fall back to creator / admin profile picture
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(chat.CreatorPublicKey)) candidates.Add(chat.CreatorPublicKey);
                foreach (var a in chat.AdminPublicKeys)
                    if (!candidates.Contains(a, StringComparer.OrdinalIgnoreCase)) candidates.Add(a);

                foreach (var pubKey in candidates)
                {
                    var user = await _storageService.GetUserByPublicKeyAsync(pubKey);
                    if (user?.LocalAvatarPath is { } cached && File.Exists(cached))
                    {
                        RxApp.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = cached);
                        break;
                    }

                    var metadata = await _nostrService.FetchUserMetadataAsync(pubKey);
                    if (string.IsNullOrEmpty(metadata?.Picture)) continue;

                    var localPath = await DownloadAvatarToCacheAsync(metadata.Picture, pubKey[..16]);
                    if (localPath == null) continue;

                    if (user != null)
                    {
                        user.AvatarUrl = metadata.Picture;
                        user.LocalAvatarPath = localPath;
                        user.DisplayName = metadata.GetDisplayName();
                        user.LastUpdatedAt = DateTime.UtcNow;
                        await _storageService.SaveUserAsync(user);
                    }

                    RxApp.MainThreadScheduler.Schedule(() => chatItem.LocalAvatarPath = localPath);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh group avatar for chat {ChatId}", chatItem.Id[..Math.Min(8, chatItem.Id.Length)]);
            }
        }
    }

    public void ClearChats()
    {
        Chats.Clear();
        SelectedChat = null;
    }

    private async Task LookupKeyPackageAsync()
    {
        if (string.IsNullOrWhiteSpace(NewChatPublicKey) || _nostrService == null)
        {
            return;
        }

        var pubKey = NewChatPublicKey.Trim();
        _logger.LogInformation("Looking up KeyPackage for: {PubKey}", pubKey[..Math.Min(16, pubKey.Length)] + "...");

        IsLookingUpKeyPackage = true;
        KeyPackageStatus = "Looking up KeyPackages...";
        HasKeyPackage = false;
        KeyPackageCreatedAt = null;
        KeyPackageRelays = null;
        FoundKeyPackages.Clear();
        SelectedKeyPackage = null;

        try
        {
            // Convert npub to hex if needed
            var publicKeyHex = pubKey;
            if (pubKey.StartsWith("npub1"))
            {
                try
                {
                    var data = Core.Crypto.Bech32.Decode(pubKey, out var hrp);
                    if (hrp == "npub" && data.Length == 32)
                    {
                        publicKeyHex = Convert.ToHexString(data).ToLowerInvariant();
                    }
                }
                catch
                {
                    KeyPackageStatus = "Invalid npub format";
                    return;
                }
            }

            var keyPackages = await _nostrService.FetchKeyPackagesAsync(publicKeyHex);
            var keyPackageList = keyPackages.OrderByDescending(k => k.CreatedAt).ToList();

            if (keyPackageList.Count > 0)
            {
                HasKeyPackage = true;
                foreach (var kp in keyPackageList)
                {
                    FoundKeyPackages.Add(new KeyPackageItemViewModel(kp));
                }

                // Auto-select the most recent SUPPORTED KeyPackage
                var supportedKp = FoundKeyPackages.FirstOrDefault(kp => kp.IsSupported);
                SelectedKeyPackage = supportedKp;
                HasMultipleKeyPackages = FoundKeyPackages.Count(kp => kp.IsSupported) > 1;

                var supportedCount = keyPackageList.Count(kp => kp.IsCipherSuiteSupported);
                var unsupportedCount = keyPackageList.Count - supportedCount;

                var latestPackage = keyPackageList.First();
                KeyPackageCreatedAt = latestPackage.CreatedAt;
                KeyPackageRelays = latestPackage.RelayUrls.Count > 0
                    ? string.Join(", ", latestPackage.RelayUrls.Take(3))
                    : "Unknown relay";

                if (unsupportedCount > 0 && supportedCount == 0)
                {
                    KeyPackageStatus = $"Found {keyPackageList.Count} KeyPackage(s) — none use a supported cipher suite";
                    _logger.LogWarning("Found {Count} KeyPackage(s) for {PubKey} but none use a supported cipher suite (all use unsupported suites)",
                        keyPackageList.Count, pubKey[..Math.Min(16, pubKey.Length)]);
                }
                else if (unsupportedCount > 0)
                {
                    KeyPackageStatus = $"Found {keyPackageList.Count} KeyPackage(s) ({unsupportedCount} unsupported)";
                    _logger.LogInformation("Found {Count} KeyPackage(s) for {PubKey} ({Unsupported} unsupported cipher suite(s))",
                        keyPackageList.Count, pubKey[..Math.Min(16, pubKey.Length)], unsupportedCount);
                }
                else
                {
                    KeyPackageStatus = $"Found {keyPackageList.Count} KeyPackage(s)";
                    _logger.LogInformation("Found {Count} KeyPackage(s) for {PubKey}", keyPackageList.Count, pubKey[..Math.Min(16, pubKey.Length)]);
                }
            }
            else
            {
                HasKeyPackage = false;
                KeyPackageStatus = "No KeyPackages found - user cannot join MLS groups";
                _logger.LogWarning("No KeyPackage found for {PubKey}", pubKey[..Math.Min(16, pubKey.Length)]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup KeyPackage");
            KeyPackageStatus = $"Lookup failed: {ex.Message}";
            HasKeyPackage = false;
        }
        finally
        {
            IsLookingUpKeyPackage = false;
        }
    }

    private async Task LookupGroupKeyPackagesAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupMembers) || _nostrService == null)
        {
            return;
        }

        _logger.LogInformation("Looking up KeyPackages for group members");

        IsLookingUpGroupKeyPackages = true;
        GroupKeyPackageStatus = "Looking up KeyPackages...";

        try
        {
            var keys = NewGroupMembers.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<string>();
            var foundCount = 0;
            var notFoundCount = 0;

            foreach (var key in keys)
            {
                var pubKey = key.Trim();
                if (string.IsNullOrEmpty(pubKey)) continue;

                // Convert npub to hex if needed
                var publicKeyHex = pubKey;
                if (pubKey.StartsWith("npub1"))
                {
                    try
                    {
                        var data = Core.Crypto.Bech32.Decode(pubKey, out var hrp);
                        if (hrp == "npub" && data.Length == 32)
                        {
                            publicKeyHex = Convert.ToHexString(data).ToLowerInvariant();
                        }
                    }
                    catch
                    {
                        results.Add($"{pubKey[..Math.Min(12, pubKey.Length)]}... - Invalid npub");
                        notFoundCount++;
                        continue;
                    }
                }

                var keyPackages = await _nostrService.FetchKeyPackagesAsync(publicKeyHex);
                var keyPackageList = keyPackages.ToList();

                if (keyPackageList.Count > 0)
                {
                    var latest = keyPackageList.OrderByDescending(k => k.CreatedAt).First();
                    results.Add($"{pubKey[..Math.Min(12, pubKey.Length)]}... - Found (created {latest.CreatedAt:g})");
                    foundCount++;
                }
                else
                {
                    results.Add($"{pubKey[..Math.Min(12, pubKey.Length)]}... - No KeyPackage");
                    notFoundCount++;
                }
            }

            GroupKeyPackageStatus = $"Found: {foundCount}, Missing: {notFoundCount}\n" + string.Join("\n", results);
            _logger.LogInformation("Group KeyPackage lookup complete: {Found} found, {Missing} missing", foundCount, notFoundCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lookup group KeyPackages");
            GroupKeyPackageStatus = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsLookingUpGroupKeyPackages = false;
        }
    }

    private void PopulateSelectableRelays()
    {
        SelectableRelays.Clear();

        if (_nostrService == null) return;

        var relayUrls = _nostrService.ConnectedRelayUrls;
        if (relayUrls == null) return;

        foreach (var url in relayUrls)
        {
            SelectableRelays.Add(new RelaySelectionItemViewModel(url, isSelected: true));
        }

        SelectedRelayCount = SelectableRelays.Count;

        // Subscribe after all items are added to avoid triggering during construction
        foreach (var item in SelectableRelays)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RelaySelectionItemViewModel.IsSelected))
                    SelectedRelayCount = SelectableRelays.Count(r => r.IsSelected);
            };
        }
    }

    private string[] GetSelectedRelayUrls()
    {
        return SelectableRelays
            .Where(r => r.IsSelected)
            .Select(r => r.Url)
            .ToArray();
    }

    private async Task CreateNewChatAsync()
    {
        if (string.IsNullOrWhiteSpace(NewChatPublicKey))
        {
            NewChatError = "Please enter a public key (npub or hex)";
            _logger.LogWarning("CreateNewChat: empty public key");
            return;
        }

        if (_mlsService == null || _nostrService == null)
        {
            NewChatError = "MLS or Nostr service not available";
            _logger.LogWarning("CreateNewChat: MLS or Nostr service is null");
            return;
        }

        var pubKey = NewChatPublicKey.Trim();
        _logger.LogInformation("Creating new chat with public key: {PubKey}", pubKey[..Math.Min(16, pubKey.Length)] + "...");

        try
        {
            // Convert npub to hex if needed
            var publicKeyHex = pubKey;
            if (pubKey.StartsWith("npub1"))
            {
                try
                {
                    var data = Core.Crypto.Bech32.Decode(pubKey, out var hrp);
                    if (hrp == "npub" && data.Length == 32)
                    {
                        publicKeyHex = Convert.ToHexString(data).ToLowerInvariant();
                    }
                }
                catch (Exception ex)
                {
                    NewChatError = "Invalid npub format";
                    _logger.LogWarning(ex, "CreateNewChat: failed to decode npub");
                    return;
                }
            }

            CreateProgress = "Resolving identity...";

            // Resolve chat name: user-provided > profile metadata > truncated key
            var chatName = NewChatName;
            if (string.IsNullOrWhiteSpace(chatName))
            {
                try
                {
                    var metadata = await _messageService.FetchAndCacheProfileAsync(publicKeyHex);
                    chatName = metadata?.DisplayName ?? metadata?.Name;
                }
                catch
                {
                    // Metadata fetch is optional
                }
                if (string.IsNullOrWhiteSpace(chatName))
                {
                    chatName = $"Chat with {pubKey[..Math.Min(12, pubKey.Length)]}...";
                }
            }

            // Get current user for signing
            var currentUser = await _storageService.GetCurrentUserAsync();
            if (currentUser == null || string.IsNullOrEmpty(currentUser.PublicKeyHex))
            {
                NewChatError = "Not logged in";
                _logger.LogWarning("CreateNewChat: no current user");
                return;
            }

            // Use the pre-selected KeyPackage from auto-lookup
            var keyPackage = SelectedKeyPackage?.KeyPackage;
            if (keyPackage == null)
            {
                NewChatError = "No KeyPackage selected — look up the user's KeyPackages first";
                _logger.LogWarning("CreateNewChat: no KeyPackage selected");
                return;
            }

            if (!keyPackage.IsCipherSuiteSupported)
            {
                NewChatError = $"Unsupported cipher suite 0x{keyPackage.CiphersuiteId:x4} ({keyPackage.CipherSuiteName}). Select a KeyPackage with a supported cipher suite.";
                _logger.LogWarning("CreateNewChat: unsupported cipher suite 0x{CipherSuite:x4} on selected KeyPackage",
                    keyPackage.CiphersuiteId);
                return;
            }
            _logger.LogDebug("Using pre-selected KeyPackage {EventId} for {PubKey}",
                keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "unknown",
                publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

            // Create MLS group (only after we've confirmed a KeyPackage exists)
            CreateProgress = "Creating MLS group...";
            _logger.LogDebug("Creating MLS group for chat...");
            var selectedRelays = GetSelectedRelayUrls();
            if (selectedRelays.Length == 0)
            {
                _logger.LogError("Cannot create group: no relays selected");
                NewChatError = "Select at least one relay";
                return;
            }
            var groupInfo = await _mlsService.CreateGroupAsync(chatName, selectedRelays);
            var nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId);
            var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();

            var chat = new Chat
            {
                Id = groupIdHex,
                Name = chatName,
                Type = ChatType.Group,
                ParticipantPublicKeys = new List<string>(groupInfo.MemberPublicKeys),
                MlsGroupId = groupInfo.GroupId,
                NostrGroupId = nostrGroupId,
                MlsEpoch = groupInfo.Epoch,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            _logger.LogInformation("Created MLS group with ID: {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

            // Add member to MLS group
            CreateProgress = "Adding member...";
            _logger.LogDebug("Adding member to MLS group");
            var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);

            // MIP-02: For initial group creation (epoch 0), do NOT publish the Commit.
            // There are no existing members who need it — the joiner gets full state via Welcome.
            // Publishing it would confuse the joiner (encrypted with epoch 0 key they don't have).
            // For adding members to existing groups (epoch > 0), publish Commit first and wait.
            if (groupInfo.Epoch > 0 && welcome.CommitData != null && welcome.CommitData.Length > 0)
            {
                _logger.LogDebug("Publishing Commit to Nostr (kind 445) — existing group, epoch {Epoch}", groupInfo.Epoch);
                try
                {
                    var commitEventJson = await _mlsService.EncryptCommitAsync(
                        chat.MlsGroupId, welcome.CommitData);
                    var commitEventId = await _nostrService.PublishRawEventJsonAsync(commitEventJson);
                    _logger.LogInformation("Published Commit {EventId} for group {GroupId}",
                        commitEventId[..Math.Min(16, commitEventId.Length)], groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                }
                catch (NotSupportedException)
                {
                    // Rust backend: publish commit data directly (handles its own encryption)
                    var commitEventId = await _nostrService.PublishCommitAsync(
                        welcome.CommitData, groupIdHex, currentUser.PrivateKeyHex);
                    _logger.LogInformation("Published Commit {EventId} for group {GroupId} (legacy)",
                        commitEventId[..Math.Min(16, commitEventId.Length)], groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                }
            }
            else
            {
                _logger.LogInformation("Skipping Commit publish for initial group creation (epoch 0)");
            }

            // Publish Welcome (kind 444)
            CreateProgress = "Publishing invite...";
            _logger.LogDebug("Publishing Welcome message to Nostr (kind 444)");
            var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                welcome.WelcomeData, publicKeyHex, currentUser.PrivateKeyHex,
                welcome.KeyPackageEventId);
            _logger.LogInformation("Published Welcome {EventId} for {PubKey}",
                welcomeEventId[..Math.Min(16, welcomeEventId.Length)],
                publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

            // Add recipient to participant list
            if (!chat.ParticipantPublicKeys.Any(p => string.Equals(p, publicKeyHex, StringComparison.OrdinalIgnoreCase)))
            {
                chat.ParticipantPublicKeys.Add(publicKeyHex);
            }

            CreateProgress = "Saving chat...";
            await _storageService.SaveChatAsync(chat);
            _logger.LogInformation("Saved chat: {ChatId} - {ChatName}", chat.Id, chatName);

            // Subscribe to group messages using NostrGroupId (from MarmotGroupData extension)
            var subIdHex = nostrGroupId != null
                ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
                : groupIdHex; // Rust backend: NostrGroupId not available from C# side
            if (nostrGroupId == null)
                _logger.LogWarning("NostrGroupId unavailable for group {GroupId} — using MLS GroupId for subscription (Rust backend)", groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            await _nostrService.SubscribeToGroupMessagesAsync(new[] { subIdHex });

            // Add to list and select it
            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            // Close dialog
            CreateProgress = null;
            ShowNewChatDialog = false;
            NewChatError = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new chat");
            CreateProgress = null;
            NewChatError = $"Failed to create chat: {ex.Message}";
        }
    }

    private async Task CreateNewGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName))
        {
            NewGroupError = "Please enter a group name";
            _logger.LogWarning("CreateNewGroup: empty group name");
            return;
        }

        _logger.LogInformation("Creating new group: {GroupName}", NewGroupName);

        try
        {
            // Parse member public keys (comma or newline separated)
            var memberKeys = new List<string>();
            if (!string.IsNullOrWhiteSpace(NewGroupMembers))
            {
                var keys = NewGroupMembers.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var key in keys)
                {
                    var trimmedKey = key.Trim();
                    if (!string.IsNullOrEmpty(trimmedKey))
                    {
                        memberKeys.Add(trimmedKey);
                    }
                }
            }

            Chat chat;
            var invitedMembers = new List<string>();
            var inviteErrors = new List<string>();

            // Get current user for signing
            var currentUser = await _storageService.GetCurrentUserAsync();

            // Try to create group with MLS if available
            if (_mlsService != null)
            {
                _logger.LogDebug("Creating MLS group...");
                var selectedRelays = GetSelectedRelayUrls();
                if (selectedRelays.Length == 0)
                {
                    _logger.LogError("Cannot create group: no relays selected");
                    NewGroupError = "Select at least one relay";
                    return;
                }
                var groupInfo = await _mlsService.CreateGroupAsync(NewGroupName.Trim(), selectedRelays);
                var nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId);

                chat = new Chat
                {
                    Id = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant(),
                    Name = NewGroupName.Trim(),
                    Description = string.IsNullOrWhiteSpace(NewGroupDescription) ? null : NewGroupDescription.Trim(),
                    Type = ChatType.Group,
                    ParticipantPublicKeys = new List<string>(groupInfo.MemberPublicKeys),
                    MlsGroupId = groupInfo.GroupId,
                    NostrGroupId = nostrGroupId,
                    MlsEpoch = groupInfo.Epoch,
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow
                };

                _logger.LogInformation("Created MLS group with ID: {GroupId}", chat.Id);

                // Invite members if we have MLS and Nostr
                if (_nostrService != null && currentUser != null && memberKeys.Count > 0)
                {
                    _logger.LogInformation("Inviting {Count} members to group", memberKeys.Count);

                    foreach (var memberKey in memberKeys)
                    {
                        try
                        {
                            // Convert npub to hex if needed
                            var publicKeyHex = memberKey;
                            if (memberKey.StartsWith("npub1"))
                            {
                                var data = Core.Crypto.Bech32.Decode(memberKey, out var hrp);
                                if (hrp == "npub" && data.Length == 32)
                                {
                                    publicKeyHex = Convert.ToHexString(data).ToLowerInvariant();
                                }
                            }

                            _logger.LogDebug("Fetching KeyPackage for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

                            // Fetch their KeyPackage
                            var keyPackages = await _nostrService.FetchKeyPackagesAsync(publicKeyHex);
                            var keyPackage = keyPackages.FirstOrDefault();

                            if (keyPackage == null)
                            {
                                _logger.LogWarning("No KeyPackage found for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
                                inviteErrors.Add($"{memberKey[..Math.Min(12, memberKey.Length)]}... - No KeyPackage");
                                continue;
                            }

                            _logger.LogDebug("Adding member to MLS group");

                            // Add them to the MLS group
                            var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);

                            // MIP-02: For initial group creation (epoch 0), skip the Commit — only send Welcome.
                            // For existing groups (epoch > 0), publish Commit first so existing members advance.
                            if (groupInfo.Epoch > 0 && welcome.CommitData != null && welcome.CommitData.Length > 0)
                            {
                                _logger.LogDebug("Publishing Commit to Nostr (kind 445) — existing group, epoch {Epoch}", groupInfo.Epoch);
                                try
                                {
                                    var commitEventJson = await _mlsService.EncryptCommitAsync(
                                        chat.MlsGroupId, welcome.CommitData);
                                    var commitEventId = await _nostrService.PublishRawEventJsonAsync(commitEventJson);
                                    _logger.LogInformation("Published Commit {EventId} for group {GroupId}",
                                        commitEventId[..Math.Min(16, commitEventId.Length)], chat.Id[..Math.Min(16, chat.Id.Length)]);
                                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                                }
                                catch (NotSupportedException)
                                {
                                    var commitEventId = await _nostrService.PublishCommitAsync(
                                        welcome.CommitData, chat.Id, currentUser.PrivateKeyHex);
                                    _logger.LogInformation("Published Commit {EventId} for group {GroupId} (legacy)",
                                        commitEventId[..Math.Min(16, commitEventId.Length)], chat.Id[..Math.Min(16, chat.Id.Length)]);
                                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                                }
                            }
                            else if (welcome.CommitData != null && welcome.CommitData.Length > 0)
                            {
                                _logger.LogInformation("Skipping Commit publish for initial group creation (epoch {Epoch})", groupInfo.Epoch);
                            }

                            _logger.LogDebug("Publishing Welcome message to Nostr (kind 444)");

                            // Publish Welcome message to Nostr (kind 444) with KeyPackage event ID
                            var eventId = await _nostrService.PublishWelcomeAsync(
                                welcome.WelcomeData,
                                publicKeyHex,
                                currentUser.PrivateKeyHex,
                                welcome.KeyPackageEventId);  // MIP-02 requires 'e' tag with KeyPackage event ID

                            _logger.LogInformation("Published Welcome {EventId} for {PubKey} (KeyPackage: {KpId})",
                                eventId[..Math.Min(16, eventId.Length)],
                                publicKeyHex[..Math.Min(16, publicKeyHex.Length)],
                                welcome.KeyPackageEventId?[..Math.Min(16, welcome.KeyPackageEventId.Length)] ?? "none");

                            // Add to participant list
                            if (!chat.ParticipantPublicKeys.Any(p => string.Equals(p, publicKeyHex, StringComparison.OrdinalIgnoreCase)))
                            {
                                chat.ParticipantPublicKeys.Add(publicKeyHex);
                            }
                            invitedMembers.Add(memberKey[..Math.Min(12, memberKey.Length)] + "...");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to invite member {Key}", memberKey[..Math.Min(16, memberKey.Length)]);
                            inviteErrors.Add($"{memberKey[..Math.Min(12, memberKey.Length)]}... - {ex.Message}");
                        }
                    }

                    if (invitedMembers.Count > 0)
                    {
                        _logger.LogInformation("Successfully invited {Count} members", invitedMembers.Count);
                    }
                    if (inviteErrors.Count > 0)
                    {
                        _logger.LogWarning("Failed to invite {Count} members", inviteErrors.Count);
                    }
                }
            }
            else
            {
                // Fallback to local-only group
                var groupId = Guid.NewGuid().ToString();
                chat = new Chat
                {
                    Id = groupId,
                    Name = NewGroupName.Trim(),
                    Description = string.IsNullOrWhiteSpace(NewGroupDescription) ? null : NewGroupDescription.Trim(),
                    Type = ChatType.Group,
                    ParticipantPublicKeys = memberKeys,
                    MlsGroupId = Guid.Parse(groupId).ToByteArray(),
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow
                };
            }

            await _storageService.SaveChatAsync(chat);
            _logger.LogInformation("Saved group: {GroupId} - {GroupName} with {MemberCount} members",
                chat.Id, chat.Name, chat.ParticipantPublicKeys.Count);

            // Subscribe to group messages for the new group
            // Use NostrGroupId for relay subscriptions when available
            if (chat.MlsGroupId != null && chat.MlsGroupId.Length > 0 && _nostrService != null)
            {
                var subGroupIdHex = chat.NostrGroupId != null && chat.NostrGroupId.Length > 0
                    ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                await _nostrService.SubscribeToGroupMessagesAsync(new[] { subGroupIdHex });
            }

            // Show invite results if there were any issues
            if (inviteErrors.Count > 0 && invitedMembers.Count == 0)
            {
                NewGroupError = $"Group created but failed to invite members:\n{string.Join("\n", inviteErrors)}";
            }
            else if (inviteErrors.Count > 0)
            {
                NewGroupError = $"Invited {invitedMembers.Count}, failed {inviteErrors.Count}:\n{string.Join("\n", inviteErrors)}";
            }

            // Add to list and select it
            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            // Close dialog only if there were no invite errors
            if (inviteErrors.Count == 0)
            {
                ShowNewGroupDialog = false;
                NewGroupError = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new group");
            NewGroupError = $"Failed to create group: {ex.Message}";
        }
    }

    private async Task JoinGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinGroupId))
        {
            JoinGroupError = "Please enter a group ID or invite link";
            _logger.LogWarning("JoinGroup: empty group ID");
            return;
        }

        var groupId = JoinGroupId.Trim();
        _logger.LogInformation("Joining group: {GroupId}", groupId[..Math.Min(16, groupId.Length)] + "...");

        try
        {
            // Check if we already have this group (case-insensitive comparison)
            var existingChat = Chats.FirstOrDefault(c =>
                c.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

            if (existingChat != null)
            {
                // Select the existing group instead of showing error
                SelectedChat = existingChat;
                ShowJoinGroupDialog = false;
                JoinGroupError = null;
                _logger.LogInformation("Already a member of group: {GroupId}, selecting it", groupId);
                return;
            }

            // Validate group ID format (should be a GUID for our app)
            if (!Guid.TryParse(groupId, out _))
            {
                _logger.LogWarning("Non-GUID group ID provided: {GroupId}", groupId);
                // Still allow it, but log warning - might be from another client
            }

            // Create a placeholder group entry
            // Note: In a full implementation, this would:
            // 1. Subscribe to Nostr events for this group ID
            // 2. Process MLS welcome message from relay
            // 3. Fetch group metadata from relays
            var chat = new Chat
            {
                Id = groupId,
                Name = $"Group {groupId[..Math.Min(8, groupId.Length)]}...",
                Type = ChatType.Group,
                MlsGroupId = Convert.FromHexString(groupId),
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            await _storageService.SaveChatAsync(chat);
            _logger.LogInformation("Joined group: {GroupId}", chat.Id);

            // Add to list and select it
            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            // Close dialog
            ShowJoinGroupDialog = false;
            JoinGroupError = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join group");
            JoinGroupError = $"Failed to join group: {ex.Message}";
        }
    }

    private async Task LookupBotNip65Async()
    {
        if (string.IsNullOrWhiteSpace(BotNpub) || _nostrService == null)
        {
            Nip65Status = "Enter a valid npub first.";
            return;
        }

        var input = BotNpub.Trim();
        string hex;
        if (input.StartsWith("npub1"))
        {
            hex = _nostrService.NpubToHex(input) ?? string.Empty;
            if (string.IsNullOrEmpty(hex))
            {
                Nip65Status = "Invalid npub format.";
                return;
            }
        }
        else if (input.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(input, "^[0-9a-fA-F]+$"))
        {
            hex = input.ToLowerInvariant();
        }
        else
        {
            Nip65Status = "Enter a valid npub or hex key.";
            return;
        }

        IsFetchingNip65 = true;
        Nip65Status = null;
        BotNip65Relays.Clear();

        try
        {
            var relays = await _nostrService.FetchRelayListAsync(hex);
            if (relays.Count == 0)
            {
                Nip65Status = "No NIP-65 relays found for this user.";
                return;
            }

            foreach (var relay in relays)
                BotNip65Relays.Add(new RelayCheckItem(relay.Url, true));

            Nip65Status = $"Found {relays.Count} relay(s).";
            _logger.LogInformation("NIP-65 lookup found {Count} relays for {PubKey}",
                relays.Count, hex[..Math.Min(16, hex.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NIP-65 lookup failed for {PubKey}", hex[..Math.Min(16, hex.Length)]);
            Nip65Status = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsFetchingNip65 = false;
        }
    }

    private async Task CreateBotChatAsync()
    {
        if (string.IsNullOrWhiteSpace(BotNpub))
        {
            AddBotError = "Please enter a valid npub";
            return;
        }

        try
        {
            var input = BotNpub.Trim();
            string hex;

            if (input.StartsWith("npub1") && _nostrService != null)
            {
                hex = _nostrService.NpubToHex(input) ?? string.Empty;
                if (string.IsNullOrEmpty(hex))
                {
                    AddBotError = "Invalid npub format";
                    return;
                }
            }
            else if (input.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(input, "^[0-9a-fA-F]+$"))
            {
                hex = input.ToLowerInvariant();
            }
            else
            {
                AddBotError = "Enter an npub (npub1...) or 64-char hex public key";
                return;
            }

            // Collect relay URLs based on selected mode
            var relayUrls = new List<string>();
            if (BotRelayModeNip65)
            {
                relayUrls.AddRange(BotNip65Relays.Where(r => r.IsChecked).Select(r => r.Url));
                if (relayUrls.Count == 0)
                {
                    AddBotError = "No NIP-65 relays found. Click 'Look up relays' first.";
                    return;
                }
            }
            else if (BotRelayModeList)
            {
                relayUrls.AddRange(BotAvailableRelays.Where(r => r.IsChecked).Select(r => r.Url));
                if (relayUrls.Count == 0)
                {
                    AddBotError = "Select at least one relay.";
                    return;
                }
            }
            else if (BotRelayModeManual)
            {
                var manual = BotManualRelay.Trim();
                if (string.IsNullOrEmpty(manual) || (!manual.StartsWith("wss://") && !manual.StartsWith("ws://")))
                {
                    AddBotError = "Enter a valid relay URL (wss://...)";
                    return;
                }
                relayUrls.Add(manual);
            }

            // Try to fetch profile for name resolution
            try
            {
                await _messageService.FetchAndCacheProfileAsync(hex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch profile, continuing with hex name");
            }

            var chat = await _messageService.GetOrCreateBotChatAsync(hex, relayUrls);

            // Connect to bot-specific relays for receiving replies
            if (relayUrls.Count > 0 && _nostrService != null)
            {
                await _nostrService.ConnectBotRelaysAsync(relayUrls);
            }

            ShowAddBotDialog = false;
            AddBotError = null;

            // Add to list if not already present and select it
            var existingItem = Chats.FirstOrDefault(c => c.Id == chat.Id);
            if (existingItem != null)
            {
                SelectedChat = existingItem;
            }
            else
            {
                var chatItem = new ChatItemViewModel(chat);
                Chats.Insert(0, chatItem);
                SelectedChat = chatItem;
            }

            _logger.LogInformation("Linked device chat {ChatId} for {Pub}",
                chat.Id[..Math.Min(8, chat.Id.Length)], hex[..Math.Min(16, hex.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create bot chat");
            AddBotError = $"Failed: {ex.Message}";
        }
    }

    public async Task RenameChatAsync(ChatItemViewModel item, string newName)
    {
        try
        {
            item.Chat.Name = newName;
            await _storageService.SaveChatAsync(item.Chat);
            item.Name = newName;
            _logger.LogInformation("Renamed chat {ChatId} to '{Name}'", item.Id, newName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename chat {ChatId}", item.Id);
        }
    }

    private async Task DeleteChatAsync()
    {
        if (ChatToDelete == null)
        {
            return;
        }

        var chatId = ChatToDelete.Id;
        var chatName = ChatToDelete.Name;
        _logger.LogInformation("Deleting chat: {ChatId} - {ChatName}", chatId, chatName);

        try
        {
            // Delete from storage
            await _storageService.DeleteChatAsync(chatId);

            // Remove from UI
            var chatToRemove = Chats.FirstOrDefault(c => c.Id == chatId);
            if (chatToRemove != null)
            {
                Chats.Remove(chatToRemove);

                // Clear selection if this was the selected chat
                if (SelectedChat?.Id == chatId)
                {
                    SelectedChat = null;
                }
            }

            _logger.LogInformation("Chat deleted successfully: {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat: {ChatId}", chatId);
        }
        finally
        {
            ShowDeleteChatDialog = false;
            ChatToDelete = null;
        }
    }

    private async Task ResetGroupAsync()
    {
        if (GroupToReset == null) return;

        var chatId = GroupToReset.Id;
        var chatName = GroupToReset.Name;
        _logger.LogInformation("Resetting group: {ChatId} - {ChatName}", chatId, chatName);

        try
        {
            await _messageService.ResetGroupAsync(chatId);

            // Remove from UI
            var chatToRemove = Chats.FirstOrDefault(c => c.Id == chatId);
            if (chatToRemove != null)
            {
                Chats.Remove(chatToRemove);
                if (SelectedChat?.Id == chatId)
                    SelectedChat = null;
            }

            StatusMessage = $"Group \"{chatName}\" reset. Rescanning for invites...";
            _logger.LogInformation("Group reset successful, rescanning for invites");

            // Trigger rescan to find the original welcome
            await RescanInvitesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset group: {ChatId}", chatId);
            StatusMessage = $"Failed to reset group: {ex.Message}";
        }
        finally
        {
            ShowResetGroupDialog = false;
            GroupToReset = null;
        }
    }

    private void OnChatUpdated(Chat chat)
    {
        var existingInChats = Chats.FirstOrDefault(c => c.Id == chat.Id);
        var existingInArchived = ArchivedChats.FirstOrDefault(c => c.Id == chat.Id);

        var isParticipant = _currentUserPubKeyHex == null ||
            chat.ParticipantPublicKeys.Any(p => string.Equals(p, _currentUserPubKeyHex, StringComparison.OrdinalIgnoreCase));

        if (chat.IsArchived)
        {
            // Move to archived if it was in main list
            if (existingInChats != null)
                Chats.Remove(existingInChats);

            if (existingInArchived != null)
            {
                existingInArchived.Update(chat);
            }
            else
            {
                ArchivedChats.Add(new ChatItemViewModel(chat));
            }
            ArchivedChatsCount = ArchivedChats.Count;
        }
        else
        {
            // Move to main list if it was archived
            if (existingInArchived != null)
            {
                ArchivedChats.Remove(existingInArchived);
                ArchivedChatsCount = ArchivedChats.Count;
            }

            if (existingInChats != null)
            {
                existingInChats.Update(chat);
                existingInChats.NeedsRepair = !isParticipant;
                // Re-sort if needed
                var index = Chats.IndexOf(existingInChats);
                var newIndex = GetInsertIndex(chat);
                if (index != newIndex && newIndex < Chats.Count)
                {
                    Chats.Move(index, newIndex > index ? newIndex - 1 : newIndex);
                }
            }
            else
            {
                var newItem = new ChatItemViewModel(chat);
                newItem.NeedsRepair = !isParticipant;
                var insertIndex = GetInsertIndex(chat);
                Chats.Insert(insertIndex, newItem);
            }
        }
    }

    private int GetInsertIndex(Chat chat)
    {
        if (chat.IsPinned)
        {
            return Chats.TakeWhile(c => c.IsPinned && c.LastActivityAt > chat.LastActivityAt).Count();
        }

        var pinnedCount = Chats.Count(c => c.IsPinned);
        return pinnedCount + Chats.Skip(pinnedCount).TakeWhile(c => c.LastActivityAt > chat.LastActivityAt).Count();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var chat in Chats)
            {
                chat.IsVisible = true;
            }
        }
        else
        {
            var filter = SearchText.ToLowerInvariant();
            foreach (var chat in Chats)
            {
                chat.IsVisible = chat.Name.ToLowerInvariant().Contains(filter) ||
                                 (chat.LastMessagePreview?.ToLowerInvariant().Contains(filter) ?? false);
            }
        }
    }

    private void OnNewInvite(PendingInvite invite)
    {
        // Deduplicate by NostrEventId (same welcome event arrives from multiple relays)
        if (!string.IsNullOrEmpty(invite.NostrEventId) &&
            PendingInvites.Any(i => i.NostrEventId == invite.NostrEventId))
            return;

        // Fallback: also check by internal Id
        if (PendingInvites.Any(i => i.Id == invite.Id))
            return;

        PendingInvites.Insert(0, new PendingInviteItemViewModel(invite));
        PendingInviteCount = PendingInvites.Count;
    }

    private async Task AcceptInviteAsync(PendingInviteItemViewModel inviteVm)
    {
        _logger.LogInformation("Accepting invite: {InviteId}", inviteVm.Id);

        inviteVm.IsAccepting = true;
        StatusMessage = "Accepting invite...";

        try
        {
            var chat = await _messageService.AcceptInviteAsync(inviteVm.Id);

            // Subscribe to group messages for the newly accepted group
            // Use NostrGroupId for relay subscriptions when available
            if (chat.MlsGroupId != null && chat.MlsGroupId.Length > 0 && _nostrService != null)
            {
                var subGroupId = chat.NostrGroupId != null && chat.NostrGroupId.Length > 0
                    ? Convert.ToHexString(chat.NostrGroupId).ToLowerInvariant()
                    : Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                await _nostrService.SubscribeToGroupMessagesAsync(new[] { subGroupId });
            }

            inviteVm.IsAccepting = false;
            PendingInvites.Remove(inviteVm);
            PendingInviteCount = PendingInvites.Count;
            StatusMessage = null;

            // Select the new chat
            var chatItem = Chats.FirstOrDefault(c => c.Id == chat.Id);
            if (chatItem != null)
            {
                SelectedChat = chatItem;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept invite: {InviteId}", inviteVm.Id);
            StatusMessage = $"Failed to accept invite: {ex.Message}";
            inviteVm.IsAccepting = false;
        }
    }

    private async Task DeclineInviteAsync(PendingInviteItemViewModel inviteVm)
    {
        _logger.LogInformation("Declining invite: {InviteId}", inviteVm.Id);

        inviteVm.IsDeclining = true;
        StatusMessage = "Declining invite...";

        try
        {
            await _messageService.DeclineInviteAsync(inviteVm.Id);

            PendingInvites.Remove(inviteVm);
            PendingInviteCount = PendingInvites.Count;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decline invite: {InviteId}", inviteVm.Id);
            StatusMessage = $"Failed to decline invite: {ex.Message}";
            inviteVm.IsDeclining = false;
        }
    }

    private async Task DismissSkippedInviteNoticeAsync()
    {
        _logger.LogInformation("Dismissing skipped invite notice (count was {Count})", SkippedInviteCount);
        await _storageService.ResetSkippedInviteCountAsync();
        SkippedInviteCount = 0;
    }

    private async Task RescanInvitesAsync()
    {
        _logger.LogInformation("Rescanning for invites...");

        IsRescanningInvites = true;
        StatusMessage = "Scanning relays for invites...";

        try
        {
            // Re-subscribe to kind 444 and wait for relay responses
            await _messageService.RescanInvitesAsync();

            // Reload invites from storage (includes any newly arrived ones)
            var invites = await _messageService.GetPendingInvitesAsync();
            PendingInvites.Clear();
            foreach (var invite in invites)
            {
                PendingInvites.Add(new PendingInviteItemViewModel(invite));
            }
            PendingInviteCount = PendingInvites.Count;

            StatusMessage = PendingInviteCount > 0
                ? $"Found {PendingInviteCount} invite(s)"
                : "No invites found";

            _logger.LogInformation("Rescan complete: {Count} invite(s)", PendingInviteCount);

            // Clear status after a delay
            await Task.Delay(3000);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rescan invites");
            StatusMessage = $"Rescan failed: {ex.Message}";
        }
        finally
        {
            IsRescanningInvites = false;
        }
    }
}

public class ChatItemViewModel : ViewModelBase
{
    public string Id { get; }

    [Reactive] public string Name { get; set; } = string.Empty;
    [Reactive] public string? AvatarUrl { get; set; }
    [Reactive] public string? LocalAvatarPath { get; set; }
    [Reactive] public string? LastMessagePreview { get; set; }
    [Reactive] public DateTime LastActivityAt { get; set; }
    [Reactive] public int UnreadCount { get; set; }
    public bool HasUnread => UnreadCount > 0;
    [Reactive] public bool IsPinned { get; set; }
    [Reactive] public bool IsMuted { get; set; }
    [Reactive] public bool IsGroup { get; set; }
    [Reactive] public bool IsBot { get; set; }
    [Reactive] public bool IsVisible { get; set; } = true;
    [Reactive] public bool NeedsRepair { get; set; }

    public Chat Chat { get; private set; }

    public ChatItemViewModel(Chat chat)
    {
        Chat = chat;
        Id = chat.Id;
        Update(chat);
    }

    public void Update(Chat chat)
    {
        Chat = chat;
        Name = chat.Name;
        AvatarUrl = chat.AvatarUrl;
        LastMessagePreview = chat.LastMessage?.Content;
        LastActivityAt = chat.LastActivityAt;
        UnreadCount = chat.UnreadCount;
        this.RaisePropertyChanged(nameof(HasUnread));
        IsPinned = chat.IsPinned;
        IsMuted = chat.IsMuted;
        IsGroup = chat.Type == ChatType.Group;
        IsBot = chat.Type == ChatType.Bot;
    }
}

public class FollowContactViewModel : ViewModelBase
{
    public string PublicKeyHex { get; }
    public string Npub { get; }
    [Reactive] public string? DisplayName { get; set; }
    [Reactive] public string? Petname { get; set; }
    [Reactive] public string? LocalAvatarPath { get; set; }
    [Reactive] public bool IsVisible { get; set; } = true;

    public string ShownName => !string.IsNullOrWhiteSpace(Petname) ? Petname!
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
        : $"{Npub[..12]}…";

    public string Initial =>
        !string.IsNullOrWhiteSpace(ShownName) && char.IsLetterOrDigit(ShownName[0])
            ? ShownName[..1].ToUpper()
            : "?";

    public FollowContactViewModel(string publicKeyHex, string npub, string? petname, string? displayName, string? localAvatarPath)
    {
        PublicKeyHex = publicKeyHex;
        Npub = npub;
        Petname = petname;
        DisplayName = displayName;
        LocalAvatarPath = localAvatarPath;

        this.WhenAnyValue(x => x.DisplayName, x => x.Petname)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(ShownName));
                this.RaisePropertyChanged(nameof(Initial));
            });
    }
}

public class RelayCheckItem : ViewModelBase
{
    public string Url { get; }
    [Reactive] public bool IsChecked { get; set; }

    public RelayCheckItem(string url, bool isChecked = false)
    {
        Url = url;
        IsChecked = isChecked;
    }
}

public class PendingInviteItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string NostrEventId { get; }
    public string SenderName { get; }
    public string? GroupId { get; }
    public DateTime ReceivedAt { get; }
    public string SenderPublicKey { get; }
    public string SenderInitial { get; }
    public string TimeAgo { get; }

    [Reactive] public bool IsAccepting { get; set; }
    [Reactive] public bool IsDeclining { get; set; }

    private ObservableAsPropertyHelper<bool>? _isProcessing;
    public bool IsProcessing => _isProcessing?.Value ?? false;

    public PendingInviteItemViewModel(PendingInvite invite)
    {
        _isProcessing = this.WhenAnyValue(x => x.IsAccepting, x => x.IsDeclining, (a, d) => a || d)
            .ToProperty(this, x => x.IsProcessing);

        Id = invite.Id;
        NostrEventId = invite.NostrEventId;
        SenderPublicKey = invite.SenderPublicKey;
        GroupId = invite.GroupId;
        ReceivedAt = invite.ReceivedAt;

        SenderName = !string.IsNullOrEmpty(invite.SenderDisplayName)
            ? invite.SenderDisplayName
            : "Anonymous";

        SenderInitial = !string.IsNullOrEmpty(invite.SenderDisplayName)
            ? invite.SenderDisplayName[..1].ToUpperInvariant()
            : "?";

        var elapsed = DateTime.UtcNow - invite.ReceivedAt;
        TimeAgo = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";
    }
}

public class KeyPackageItemViewModel : ViewModelBase
{
    /// <summary>The underlying KeyPackage model.</summary>
    public KeyPackage KeyPackage { get; }

    /// <summary>Relative time display, e.g. "2h ago".</summary>
    public string CreatedAtDisplay { get; }

    /// <summary>Ciphersuite as hex string, e.g. "0x0001".</summary>
    public string CiphersuiteDisplay { get; }

    /// <summary>Human-readable cipher suite name.</summary>
    public string CiphersuiteName { get; }

    /// <summary>Whether this KeyPackage uses a supported cipher suite.</summary>
    public bool IsSupported { get; }

    /// <summary>First relay URL where this KeyPackage was found.</summary>
    public string RelaySource { get; }

    /// <summary>Truncated event ID for reference.</summary>
    public string KeyPackageRef { get; }

    /// <summary>Compact summary for display, e.g. "2h ago via relay.example.com".</summary>
    public string Summary { get; }

    public KeyPackageItemViewModel(KeyPackage keyPackage)
    {
        KeyPackage = keyPackage;

        var elapsed = DateTime.UtcNow - keyPackage.CreatedAt;
        CreatedAtDisplay = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";

        CiphersuiteDisplay = $"0x{keyPackage.CiphersuiteId:x4}";
        CiphersuiteName = keyPackage.CipherSuiteName;
        IsSupported = keyPackage.IsCipherSuiteSupported;

        RelaySource = keyPackage.RelayUrls.Count > 0
            ? keyPackage.RelayUrls[0]
            : "Unknown";

        KeyPackageRef = !string.IsNullOrEmpty(keyPackage.NostrEventId) && keyPackage.NostrEventId.Length > 12
            ? keyPackage.NostrEventId[..12] + "..."
            : keyPackage.NostrEventId ?? "N/A";

        var relayShort = RelaySource.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
        Summary = $"{CreatedAtDisplay} via {relayShort}";
    }
}

public class RelaySelectionItemViewModel : ViewModelBase
{
    public string Url { get; }
    public string DisplayUrl => Url.Replace("wss://", "").Replace("ws://", "").TrimEnd('/');
    [Reactive] public bool IsSelected { get; set; }

    public RelaySelectionItemViewModel(string url, bool isSelected = true)
    {
        Url = url;
        IsSelected = isSelected;
    }
}
