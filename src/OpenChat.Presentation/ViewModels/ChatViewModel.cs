using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core.Audio;
using OpenChat.Core.Crypto;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.Services;

namespace OpenChat.Presentation.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly ILogger<ChatViewModel> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly INostrService _nostrService;
    private readonly IMlsService _mlsService;
    private readonly IPlatformClipboard _clipboard;
    private IDisposable? _messageSubscription;
    private IDisposable? _reactionSubscription;
    private IDisposable? _recordingTimerSubscription;
    private Chat? _currentChat;
    private string? _currentUserPrivateKeyHex;
    private string? _currentUserPublicKeyHex;
    private DateTimeOffset? _fetchBoundary;

    // MIP-04 media cache (messageId -> decrypted bytes)
    private readonly Dictionary<string, byte[]> _mediaCache = new();

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    /// <summary>
    /// Raised when the view should scroll to the bottom of the message list.
    /// </summary>
    public event EventHandler? ScrollToBottomRequested;

    [Reactive] public string? ChatId { get; private set; }
    [Reactive] public string ChatName { get; set; } = string.Empty;
    [Reactive] public string? ChatAvatarUrl { get; set; }
    [Reactive] public bool IsGroup { get; set; }
    [Reactive] public int ParticipantCount { get; set; }
    [Reactive] public string MessageText { get; set; } = string.Empty;
    [Reactive] public bool IsSending { get; set; }
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public bool HasChat { get; set; }

    // Metadata panel
    [Reactive] public bool ShowMetadataPanel { get; set; }
    [Reactive] public bool IsLoadingMetadata { get; set; }
    [Reactive] public UserMetadata? ContactMetadata { get; set; }
    [Reactive] public string? ContactPublicKey { get; set; }

    // Computed metadata properties for proper binding updates
    [Reactive] public string? MetadataDisplayName { get; set; }
    [Reactive] public string? MetadataName { get; set; }
    [Reactive] public string? MetadataUsername { get; set; }
    [Reactive] public string? MetadataAbout { get; set; }
    [Reactive] public string? MetadataPicture { get; set; }
    [Reactive] public string? MetadataBanner { get; set; }
    [Reactive] public string? MetadataWebsite { get; set; }
    [Reactive] public string? MetadataNip05 { get; set; }
    [Reactive] public string? MetadataLud16 { get; set; }
    [Reactive] public string? MetadataNpub { get; set; }
    [Reactive] public bool HasMetadata { get; set; }

    // Group member list
    public ObservableCollection<GroupMemberViewModel> GroupMembers { get; } = new();
    [Reactive] public bool IsLoadingMembers { get; set; }
    [Reactive] public bool IsCurrentUserAdmin { get; set; }

    // Load older messages
    [Reactive] public bool IsLoadingOlder { get; set; }
    [Reactive] public bool CanLoadOlder { get; set; } = true;

    // Invite to group
    [Reactive] public bool ShowInviteDialog { get; set; }
    [Reactive] public string InvitePublicKey { get; set; } = string.Empty;
    [Reactive] public string? InviteError { get; set; }
    [Reactive] public string? InviteSuccess { get; set; }
    [Reactive] public string? GroupInviteLink { get; set; }

    // MIP-04 state (controls attach/mic button visibility)
    [Reactive] public bool IsMip04Enabled { get; set; }

    // Voice recording state
    [Reactive] public bool IsRecording { get; set; }
    [Reactive] public string RecordingDuration { get; set; } = "0:00";
    [Reactive] public bool IsSendingVoice { get; set; }

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadOlderMessagesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatInfoCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMetadataPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowInviteDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseInviteDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> SendInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyGroupLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> AttachFileCommand { get; }

    // Reply state
    [Reactive] public Message? ReplyingToMessage { get; set; }
    [Reactive] public string? ReplyPreviewText { get; set; }
    [Reactive] public string? ReplyPreviewSender { get; set; }
    public ReactiveCommand<Unit, Unit> CancelReplyCommand { get; }

    // Sending image state
    [Reactive] public bool IsSendingImage { get; set; }

    // Upload progress status (shown in chat area during file/voice sends)
    [Reactive] public string? UploadStatus { get; set; }

    // Static service references (set by platform startup)
    public static IAudioRecordingService? AudioRecordingService { get; set; }
    public static IAudioPlaybackService? AudioPlaybackService { get; set; }
    public static IMediaUploadService? MediaUploadService { get; set; }

    /// <summary>
    /// Platform-specific file picker. Returns (bytes, fileName, mimeType) or null if cancelled.
    /// Set by Desktop App.axaml.cs or Android MainActivity.
    /// </summary>
    public static Func<Task<(byte[] Data, string FileName, string MimeType)?>>? FilePickerFunc { get; set; }

    public ChatViewModel(IMessageService messageService, IStorageService storageService, INostrService nostrService, IMlsService mlsService, IPlatformClipboard clipboard, IMediaDownloadService? mediaDownloadService = null)
    {
        _logger = LoggingConfiguration.CreateLogger<ChatViewModel>();
        _messageService = messageService;
        _storageService = storageService;
        _nostrService = nostrService;
        _mlsService = mlsService;
        _clipboard = clipboard;

        // Set static service references for MessageViewModel media loading
        MessageViewModel.MediaDownloadService = mediaDownloadService ?? new MediaDownloadService();
        MessageViewModel.MlsServiceRef = mlsService;
        MessageViewModel.StorageServiceRef = storageService;
        MessageViewModel.MessageServiceRef = messageService;

        // Disk-backed media cache: check memory first, then disk
        var diskCache = new MediaCacheService();
        MessageViewModel.MediaCacheGet = (id) =>
        {
            if (_mediaCache.TryGetValue(id, out var bytes)) return bytes;
            var diskBytes = diskCache.GetCached(id);
            if (diskBytes != null) _mediaCache[id] = diskBytes;
            return diskBytes;
        };
        MessageViewModel.MediaCacheSet = (id, bytes) =>
        {
            _mediaCache[id] = bytes;
            diskCache.Save(id, bytes);
        };

        var canSend = this.WhenAnyValue(
            x => x.MessageText,
            x => x.IsSending,
            x => x.HasChat,
            (text, sending, hasChat) => !string.IsNullOrWhiteSpace(text) && !sending && hasChat);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreMessagesAsync);

        var canLoadOlder = this.WhenAnyValue(
            x => x.CanLoadOlder,
            x => x.IsLoadingOlder,
            x => x.HasChat,
            (canLoad, loading, hasChat) => canLoad && !loading && hasChat);
        LoadOlderMessagesCommand = ReactiveCommand.CreateFromTask(LoadOlderMessagesFromRelayAsync, canLoadOlder);

        ShowChatInfoCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowMetadataPanel = !ShowMetadataPanel;
            if (ShowMetadataPanel && ContactMetadata == null)
            {
                await LoadContactMetadataAsync();
            }
            if (ShowMetadataPanel && IsGroup && GroupMembers.Count == 0)
            {
                await LoadGroupMembersAsync();
            }
        });

        ToggleMetadataPanelCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _logger.LogDebug("Toggling metadata panel from {Current} to {New}", ShowMetadataPanel, !ShowMetadataPanel);
            ShowMetadataPanel = !ShowMetadataPanel;

            // Load metadata when panel is opened
            if (ShowMetadataPanel && ContactMetadata == null)
            {
                await LoadContactMetadataAsync();
            }
            if (ShowMetadataPanel && IsGroup && GroupMembers.Count == 0)
            {
                await LoadGroupMembersAsync();
            }
        });

        // Invite commands
        ShowInviteDialogCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogDebug("Opening invite dialog for group: {ChatId}", ChatId);
            InvitePublicKey = string.Empty;
            InviteError = null;
            InviteSuccess = null;
            GroupInviteLink = ChatId; // Use chat ID as invite link for now
            ShowInviteDialog = true;
        });

        CloseInviteDialogCommand = ReactiveCommand.Create(() =>
        {
            ShowInviteDialog = false;
            InviteError = null;
            InviteSuccess = null;
        });

        var canInvite = this.WhenAnyValue(
            x => x.InvitePublicKey,
            x => x.IsGroup,
            (key, isGroup) => isGroup && !string.IsNullOrWhiteSpace(key) && key.Length >= 8);

        SendInviteCommand = ReactiveCommand.CreateFromTask(SendInviteAsync, canInvite);

        CopyGroupLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(GroupInviteLink)) return;

            try
            {
                await _clipboard.SetTextAsync(GroupInviteLink);
                InviteSuccess = "Group ID copied to clipboard!";
                await Task.Delay(2000);
                if (ShowInviteDialog) InviteSuccess = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy group link");
            }
        });

        var canRecord = this.WhenAnyValue(
            x => x.HasChat,
            x => x.IsSendingVoice,
            x => x.IsMip04Enabled,
            (hasChat, sending, mip04) => hasChat && !sending && mip04);
        ToggleRecordingCommand = ReactiveCommand.CreateFromTask(ToggleRecordingAsync, canRecord);
        CancelRecordingCommand = ReactiveCommand.CreateFromTask(CancelRecordingAsync);

        var canAttach = this.WhenAnyValue(
            x => x.HasChat,
            x => x.IsSendingImage,
            x => x.IsMip04Enabled,
            (hasChat, sending, mip04) => hasChat && !sending && mip04);
        AttachFileCommand = ReactiveCommand.CreateFromTask(AttachAndSendFileAsync, canAttach);

        // Reply commands
        CancelReplyCommand = ReactiveCommand.Create(() =>
        {
            ReplyingToMessage = null;
            ReplyPreviewText = null;
            ReplyPreviewSender = null;
        });

        // Log errors from media commands (ReactiveCommand swallows exceptions by default)
        ToggleRecordingCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "ToggleRecordingCommand failed"));
        CancelRecordingCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "CancelRecordingCommand failed"));
        AttachFileCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "AttachFileCommand failed"));

        // Subscribe to new messages
        _messageSubscription = _messageService.NewMessages
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnNewMessage);

        // Subscribe to reaction updates
        _reactionSubscription = _messageService.ReactionUpdates
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnReactionUpdate);
    }

    public void LoadChat(Chat chat)
    {
        _logger.LogInformation("Loading chat: {ChatId} - {ChatName}", chat.Id, chat.Name);

        _currentChat = chat;
        ChatId = chat.Id;
        ChatName = chat.Name;
        ChatAvatarUrl = chat.AvatarUrl;
        IsGroup = chat.Type == ChatType.Group;
        ParticipantCount = chat.ParticipantPublicKeys.Count;
        // Admin check: if admin list exists, check membership; if empty (legacy group), allow all
        IsCurrentUserAdmin = IsGroup && _currentUserPublicKeyHex != null &&
            (chat.AdminPublicKeys.Count == 0 ||
             chat.AdminPublicKeys.Contains(_currentUserPublicKeyHex.ToLowerInvariant()));
        GroupMembers.Clear();
        HasChat = true;

        // Load MIP-04 setting for attach/mic button visibility
        LoadMip04SettingAsync();

        // Reset load-older state
        _fetchBoundary = null;
        CanLoadOlder = true;

        // Reset metadata panel state
        ShowMetadataPanel = false;
        ContactMetadata = null;
        ClearMetadataProperties();

        // Get the contact's public key for DMs and 2-participant groups
        ContactPublicKey = null;
        if (chat.ParticipantPublicKeys.Count > 0 && chat.ParticipantPublicKeys.Count <= 2)
        {
            if (_currentUserPublicKeyHex != null)
            {
                // For 1-on-1 chats (DM or MLS group), find the other participant
                var otherKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUserPublicKeyHex);
                ContactPublicKey = otherKey;
            }
            // If _currentUserPublicKeyHex is not yet set, defer resolution to LoadContactMetadataAsync

            if (ContactPublicKey != null)
                _logger.LogDebug("Contact public key: {PubKey}", ContactPublicKey[..Math.Min(16, ContactPublicKey.Length)] + "...");
            else
                _logger.LogDebug("Contact public key deferred — current user key not yet available");
        }

        LoadMessagesAsync().ConfigureAwait(false);

        // Load cached contact profile from DB so the avatar shows immediately (no relay fetch)
        if (!IsGroup && ContactPublicKey != null)
        {
            _ = LoadCachedContactProfileAsync(ContactPublicKey);
        }
        _messageService.MarkAsReadAsync(chat.Id).ConfigureAwait(false);
    }

    public void ClearChat()
    {
        _currentChat = null;
        ChatId = null;
        ChatName = string.Empty;
        ChatAvatarUrl = null;
        IsGroup = false;
        ParticipantCount = 0;
        IsCurrentUserAdmin = false;
        HasChat = false;
        _fetchBoundary = null;
        CanLoadOlder = true;
        ShowMetadataPanel = false;
        ContactMetadata = null;
        ContactPublicKey = null;
        ClearMetadataProperties();
        Messages.Clear();
        GroupMembers.Clear();
        _mediaCache.Clear();
    }

    /// <summary>
    /// Sets the current user's keys for signing Nostr events and identifying self.
    /// For signer users, privateKeyHex may be null — the public key is still needed for contact resolution.
    /// </summary>
    public void SetUserContext(string? privateKeyHex, string? publicKeyHex = null)
    {
        _currentUserPrivateKeyHex = privateKeyHex;
        _currentUserPublicKeyHex = publicKeyHex;
        _logger.LogDebug("User context set for ChatViewModel (hasPrivKey={HasPrivKey}, hasPubKey={HasPubKey})",
            privateKeyHex != null, publicKeyHex != null);
    }

    private async Task LoadCachedContactProfileAsync(string publicKeyHex)
    {
        try
        {
            var user = await _storageService.GetUserByPublicKeyAsync(publicKeyHex);
            _logger.LogDebug("LoadCachedProfile: user={Found}, avatarUrl={Avatar}, displayName={Name}",
                user != null, user?.AvatarUrl?[..Math.Min(30, user?.AvatarUrl?.Length ?? 0)], user?.DisplayName);
            if (user != null && !string.IsNullOrEmpty(user.AvatarUrl))
            {
                MetadataPicture = user.AvatarUrl;
                MetadataDisplayName = user.DisplayName;
                MetadataName = user.Username;
                MetadataNpub = user.Npub;
                MetadataAbout = user.About;
                MetadataNip05 = user.Nip05;
                HasMetadata = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load cached profile for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
        }
    }

    public async Task LoadContactMetadataAsync()
    {
        // Resolve ContactPublicKey if it wasn't set in LoadChat (e.g. _currentUserPublicKeyHex was null at that time)
        if (string.IsNullOrEmpty(ContactPublicKey) && _currentChat != null
            && _currentChat.ParticipantPublicKeys.Count > 0
            && _currentChat.ParticipantPublicKeys.Count <= 2)
        {
            if (_currentUserPublicKeyHex == null)
            {
                var currentUser = await _storageService.GetCurrentUserAsync();
                if (currentUser != null)
                    _currentUserPublicKeyHex = currentUser.PublicKeyHex;
            }

            if (_currentUserPublicKeyHex != null)
            {
                ContactPublicKey = _currentChat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUserPublicKeyHex);
            }

            // Last resort: if still null and there are 2 participants, take the last one
            ContactPublicKey ??= _currentChat.ParticipantPublicKeys.Last();

            _logger.LogDebug("Resolved contact public key in metadata load: {PubKey}",
                ContactPublicKey[..Math.Min(16, ContactPublicKey.Length)] + "...");
        }

        if (string.IsNullOrEmpty(ContactPublicKey) || IsLoadingMetadata)
            return;

        _logger.LogInformation("Loading contact metadata for: {PubKey}", ContactPublicKey[..Math.Min(16, ContactPublicKey.Length)] + "...");
        IsLoadingMetadata = true;

        try
        {
            // Convert npub to hex if needed
            var pubKeyHex = ContactPublicKey.StartsWith("npub")
                ? _nostrService.NpubToHex(ContactPublicKey)
                : ContactPublicKey;

            if (string.IsNullOrEmpty(pubKeyHex))
            {
                _logger.LogWarning("Invalid public key format: {PubKey}", ContactPublicKey);
                return;
            }

            var metadata = await _messageService.FetchAndCacheProfileAsync(pubKeyHex);
            ContactMetadata = metadata;

            // Update computed properties for UI binding
            if (metadata != null)
            {
                _logger.LogInformation("Loaded metadata: {DisplayName}", metadata.GetDisplayName());
                MetadataDisplayName = metadata.DisplayName;
                MetadataName = metadata.Name;
                MetadataUsername = metadata.Username;
                MetadataAbout = metadata.About;
                MetadataPicture = metadata.Picture;
                MetadataBanner = metadata.Banner;
                MetadataWebsite = metadata.Website;
                MetadataNip05 = metadata.Nip05;
                MetadataLud16 = metadata.Lud16;
                MetadataNpub = metadata.Npub;
                HasMetadata = true;

                _logger.LogDebug("Set computed properties - DisplayName: {DisplayName}, Name: {Name}, Npub: {Npub}",
                    MetadataDisplayName, MetadataName, MetadataNpub?[..Math.Min(20, MetadataNpub?.Length ?? 0)]);
            }
            else
            {
                ClearMetadataProperties();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load contact metadata");
            ClearMetadataProperties();
        }
        finally
        {
            IsLoadingMetadata = false;
        }
    }

    private async Task LoadGroupMembersAsync()
    {
        if (_currentChat == null || IsLoadingMembers) return;

        IsLoadingMembers = true;
        try
        {
            GroupMembers.Clear();

            foreach (var pubKeyHex in _currentChat.ParticipantPublicKeys)
            {
                var member = new GroupMemberViewModel
                {
                    PublicKeyHex = pubKeyHex,
                    DisplayName = $"{pubKeyHex[..12]}...",
                    IsCurrentUser = pubKeyHex == _currentUserPublicKeyHex,
                    IsAdmin = _currentChat.AdminPublicKeys.Contains(pubKeyHex.ToLowerInvariant())
                };
                GroupMembers.Add(member);

                // Fetch metadata in background — update the member VM when it arrives
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var metadata = await _messageService.FetchAndCacheProfileAsync(pubKeyHex);
                        if (metadata != null)
                        {
                            RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
                            {
                                member.DisplayName = metadata.GetDisplayName();
                                member.Picture = metadata.Picture;
                                if (!string.IsNullOrEmpty(metadata.Npub))
                                    member.Npub = metadata.Npub;
                                return Disposable.Empty;
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch metadata for member {PubKey}",
                            pubKeyHex[..Math.Min(16, pubKeyHex.Length)]);
                    }
                });
            }

            _logger.LogInformation("Loaded {Count} group members", GroupMembers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load group members");
        }
        finally
        {
            IsLoadingMembers = false;
        }
    }

    private void ClearMetadataProperties()
    {
        MetadataDisplayName = null;
        MetadataName = null;
        MetadataUsername = null;
        MetadataAbout = null;
        MetadataPicture = null;
        MetadataBanner = null;
        MetadataWebsite = null;
        MetadataNip05 = null;
        MetadataLud16 = null;
        MetadataNpub = null;
        HasMetadata = false;
    }

    private async Task LoadOlderMessagesFromRelayAsync()
    {
        if (ChatId == null || _currentChat == null)
            return;

        _logger.LogInformation("Loading older messages for chat {ChatId}", ChatId);
        IsLoadingOlder = true;

        try
        {
            // Initialize boundary from the oldest currently displayed message
            if (_fetchBoundary == null)
            {
                if (Messages.Count > 0)
                {
                    var oldestTimestamp = Messages.Min(m => m.Timestamp);
                    _fetchBoundary = new DateTimeOffset(oldestTimestamp, TimeSpan.Zero);
                }
                else
                {
                    _fetchBoundary = DateTimeOffset.UtcNow;
                }
                _logger.LogDebug("Initialized fetch boundary to {Boundary}", _fetchBoundary);
            }

            var result = await _messageService.LoadOlderMessagesAsync(ChatId, _fetchBoundary.Value);

            _logger.LogInformation("LoadOlderMessages returned {Count} messages, hasMore={HasMore}",
                result.Messages.Count, result.HasMore);

            // Prepend messages at the beginning of the list (oldest first)
            for (var i = 0; i < result.Messages.Count; i++)
            {
                // Check for duplicates before inserting
                var msg = result.Messages[i];
                if (Messages.Any(m => m.Id == msg.Id
                    || (!string.IsNullOrEmpty(msg.NostrEventId) && m.Message.NostrEventId == msg.NostrEventId)))
                    continue;

                Messages.Insert(i, new MessageViewModel(msg));
            }

            _fetchBoundary = result.NewBoundary;
            CanLoadOlder = result.HasMore;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load older messages for chat {ChatId}", ChatId);
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    private async Task SendInviteAsync()
    {
        if (string.IsNullOrWhiteSpace(InvitePublicKey) || _currentChat == null)
        {
            InviteError = "Please enter a valid public key";
            _logger.LogWarning("SendInvite: empty public key or no current chat");
            return;
        }

        _logger.LogInformation("Sending invite to {PubKey} for group {GroupId}",
            InvitePublicKey[..Math.Min(16, InvitePublicKey.Length)] + "...", ChatId);

        try
        {
            var pubKey = InvitePublicKey.Trim();

            // Convert npub to hex if needed for consistent storage
            string pubKeyHex;
            if (pubKey.StartsWith("npub1"))
            {
                pubKeyHex = _nostrService.NpubToHex(pubKey) ?? string.Empty;
                if (string.IsNullOrEmpty(pubKeyHex))
                {
                    InviteError = "Invalid npub format. Please check the public key.";
                    _logger.LogWarning("SendInvite: failed to convert npub to hex");
                    return;
                }
            }
            else if (pubKey.Length == 64 && pubKey.All(c => "0123456789abcdefABCDEF".Contains(c)))
            {
                pubKeyHex = pubKey.ToLowerInvariant();
            }
            else
            {
                InviteError = "Invalid public key format. Use npub1... or 64-char hex.";
                _logger.LogWarning("SendInvite: unrecognized key format (not npub, not 64-char hex)");
                return;
            }

            // Check if user is already a member (check both hex and npub formats)
            if (_currentChat.ParticipantPublicKeys.Any(pk =>
                pk.Equals(pubKeyHex, StringComparison.OrdinalIgnoreCase) ||
                pk.Equals(pubKey, StringComparison.OrdinalIgnoreCase)))
            {
                InviteError = "This user is already a member of the group";
                _logger.LogWarning("SendInvite: user {PubKey} already a member", pubKeyHex[..Math.Min(16, pubKeyHex.Length)]);
                return;
            }

            // Step 1: Fetch the invitee's KeyPackage from relays
            _logger.LogDebug("Fetching KeyPackage for {PubKey}", pubKeyHex[..16] + "...");
            InviteSuccess = "Fetching user's KeyPackage...";

            var keyPackages = await _nostrService.FetchKeyPackagesAsync(pubKeyHex);
            var keyPackage = keyPackages.FirstOrDefault();

            if (keyPackage == null)
            {
                _logger.LogWarning("No KeyPackage found for {PubKey}", pubKeyHex);
                InviteError = "No KeyPackage found for this user. They need to publish a KeyPackage first.";
                InviteSuccess = null;
                return;
            }

            if (_currentChat.MlsGroupId == null)
            {
                InviteError = "Cannot invite to a non-MLS group.";
                _logger.LogWarning("SendInvite: chat {ChatId} has no MLS group ID", ChatId);
                return;
            }

            // Step 2: Create MLS Welcome message using their KeyPackage
            _logger.LogDebug("Creating MLS Welcome message");
            InviteSuccess = "Creating encrypted invite...";

            var welcome = await _mlsService.AddMemberAsync(_currentChat.MlsGroupId, keyPackage);

            // Step 3: Publish Commit BEFORE Welcome (MIP-02/03)
            // Existing group members need the Commit to advance their epoch.
            if (welcome.CommitData != null && welcome.CommitData.Length > 0)
            {
                _logger.LogDebug("Publishing Commit to Nostr (kind 445) for existing members");
                InviteSuccess = "Publishing commit...";

                try
                {
                    var commitEventJson = await _mlsService.EncryptCommitAsync(
                        _currentChat.MlsGroupId, welcome.CommitData);
                    var commitEventId = await _nostrService.PublishRawEventJsonAsync(commitEventJson);
                    _logger.LogInformation("Published Commit {EventId} for invite to {PubKey}",
                        commitEventId[..Math.Min(16, commitEventId.Length)], pubKeyHex[..16] + "...");
                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                }
                catch (NotSupportedException)
                {
                    var groupIdHex = Convert.ToHexString(_currentChat.MlsGroupId).ToLowerInvariant();
                    var commitEventId = await _nostrService.PublishCommitAsync(
                        welcome.CommitData, groupIdHex, _currentUserPrivateKeyHex);
                    _logger.LogInformation("Published Commit {EventId} for invite to {PubKey} (legacy)",
                        commitEventId[..Math.Min(16, commitEventId.Length)], pubKeyHex[..16] + "...");
                    await _nostrService.WaitForRelayOkAsync(commitEventId);
                }
            }

            // Step 4: Publish Welcome to relays (kind 444)
            _logger.LogDebug("Publishing Welcome message to relays");
            InviteSuccess = "Publishing invite to Nostr...";

            var eventId = await _nostrService.PublishWelcomeAsync(
                welcome.WelcomeData,
                pubKeyHex,
                _currentUserPrivateKeyHex);

            _logger.LogInformation("Published Welcome message {EventId} for {PubKey}",
                eventId, pubKeyHex[..16] + "...");

            // Step 4: Add to local participants list ONLY after Welcome is published
            _currentChat.ParticipantPublicKeys.Add(pubKeyHex);
            _currentChat.LastActivityAt = DateTime.UtcNow;

            // Save updated chat
            await _storageService.SaveChatAsync(_currentChat);

            // Update participant count
            ParticipantCount = _currentChat.ParticipantPublicKeys.Count;

            _logger.LogInformation("Successfully invited {PubKey} to group {GroupId}", pubKeyHex, ChatId);

            InviteSuccess = "Invite sent successfully!";
            InvitePublicKey = string.Empty;

            // Clear success message after delay
            await Task.Delay(2000);
            if (ShowInviteDialog) InviteSuccess = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send invite");
            InviteError = $"Failed to add member: {ex.Message}";
        }
    }

    private async Task LoadMessagesAsync()
    {
        if (ChatId == null) return;

        IsLoading = true;

        try
        {
            Messages.Clear();

            var messages = await _messageService.GetMessagesAsync(ChatId);
            foreach (var message in messages)
            {
                Messages.Add(new MessageViewModel(message));
            }

            ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadMoreMessagesAsync()
    {
        if (ChatId == null) return;

        var messages = await _messageService.GetMessagesAsync(ChatId, 50, Messages.Count);

        foreach (var message in messages.Reverse())
        {
            Messages.Insert(0, new MessageViewModel(message));
        }
    }

    public void SetReplyTo(Message message)
    {
        ReplyingToMessage = message;
        ReplyPreviewText = message.Content?.Length > 100 ? message.Content[..100] + "..." : message.Content;
        ReplyPreviewSender = message.Sender?.DisplayName ?? "Anonymous";
        _logger.LogDebug("Reply set to message {MessageId} from {Sender}", message.Id, ReplyPreviewSender);
    }

    private async Task SendMessageAsync()
    {
        if (ChatId == null || string.IsNullOrWhiteSpace(MessageText)) return;

        IsSending = true;

        try
        {
            var text = MessageText;
            var replyTo = ReplyingToMessage;
            MessageText = string.Empty;
            ReplyingToMessage = null;
            ReplyPreviewText = null;
            ReplyPreviewSender = null;

            if (replyTo != null)
            {
                await _messageService.SendReplyAsync(ChatId, text, replyTo.Id);
            }
            else
            {
                await _messageService.SendMessageAsync(ChatId, text);
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    private void OnNewMessage(Message message)
    {
        if (message.ChatId != ChatId) return;

        // Avoid duplicates (by Id or NostrEventId for relay echoes)
        if (Messages.Any(m => m.Id == message.Id
            || (!string.IsNullOrEmpty(message.NostrEventId) && m.Message.NostrEventId == message.NostrEventId)))
            return;

        Messages.Add(new MessageViewModel(message));
        ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);

        if (!message.IsFromCurrentUser)
        {
            _messageService.MarkAsReadAsync(ChatId!).ConfigureAwait(false);
        }
    }

    private void OnReactionUpdate((string MessageId, string Emoji, string ReactorPublicKey) update)
    {
        var messageVm = Messages.FirstOrDefault(m => m.Id == update.MessageId);
        if (messageVm != null)
        {
            // Update the underlying model
            if (!messageVm.Message.Reactions.ContainsKey(update.Emoji))
                messageVm.Message.Reactions[update.Emoji] = new List<string>();

            if (!messageVm.Message.Reactions[update.Emoji].Contains(update.ReactorPublicKey))
                messageVm.Message.Reactions[update.Emoji].Add(update.ReactorPublicKey);

            messageVm.UpdateReactionsDisplay();
        }
    }

    private async void LoadMip04SettingAsync()
    {
        try
        {
            var setting = await _storageService.GetSettingAsync("mip04_enabled");
            IsMip04Enabled = setting == "true";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MIP-04 setting for chat");
        }
    }

    private async Task AttachAndSendFileAsync()
    {
        if (FilePickerFunc == null || _currentChat?.MlsGroupId == null)
        {
            _logger.LogWarning("AttachFile: file picker or MLS group not available");
            return;
        }

        try
        {
            var result = await FilePickerFunc();
            if (result == null) return; // User cancelled

            var (fileData, fileName, mimeType) = result.Value;
            IsSendingImage = true;

            _logger.LogInformation("AttachFile: {FileName} ({MimeType}, {Size} bytes)",
                fileName, mimeType, fileData.Length);

            UploadStatus = "Encrypting file...";

            // Compute SHA-256 of plaintext
            var sha256Hex = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(fileData)).ToLowerInvariant();

            // MIP-04 encrypt
            var mediaKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(
                _mlsService.GetMediaExporterSecret(_currentChat.MlsGroupId!),
                sha256Hex, mimeType, fileName);
            var (encrypted, nonce) = Mip04MediaCrypto.EncryptMediaFile(fileData, mediaKey, sha256Hex, mimeType, fileName);

            // Upload to Blossom
            if (MediaUploadService == null)
                throw new InvalidOperationException("Media upload service not configured");

            UploadStatus = string.IsNullOrEmpty(_currentUserPrivateKeyHex)
                ? "Waiting for signer approval..."
                : "Uploading to Blossom...";

            var uploadResult = await MediaUploadService.UploadAsync(encrypted, _currentUserPrivateKeyHex, mimeType);
            var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();

            UploadStatus = "Sending encrypted message...";

            // Determine message type from mime
            var messageType = mimeType.StartsWith("image/") ? MessageType.Image : MessageType.File;

            // Send via MessageService
            var content = messageType == MessageType.Image
                ? $"[Encrypted image: {fileName}]"
                : $"[Encrypted file: {fileName}]";

            await _messageService.SendMediaMessageAsync(
                _currentChat.Id, content, uploadResult.Url, sha256Hex, nonceHex,
                mimeType, fileName, messageType);

            _logger.LogInformation("Media sent: {FileName}, {Size} bytes encrypted", fileName, encrypted.Length);
            UploadStatus = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send media file");
            UploadStatus = $"Upload failed: {ex.Message}";
            // Clear error after 5 seconds
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) => { UploadStatus = null; return System.Reactive.Disposables.Disposable.Empty; });
            });
        }
        finally
        {
            IsSendingImage = false;
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await SendVoiceMessageAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    private async Task StartRecordingAsync()
    {
        if (AudioRecordingService == null)
        {
            _logger.LogWarning("Audio recording service not available");
            return;
        }

        try
        {
            await AudioRecordingService.StartRecordingAsync();
            IsRecording = true;
            RecordingDuration = "0:00";

            // Update recording duration every second
            _recordingTimerSubscription = Observable.Interval(TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    if (AudioRecordingService?.IsRecording == true)
                    {
                        var dur = AudioRecordingService.RecordingDuration;
                        RecordingDuration = $"{(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                    }
                });

            _logger.LogInformation("Voice recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start voice recording");
        }
    }

    private async Task SendVoiceMessageAsync()
    {
        if (AudioRecordingService == null || _currentChat == null) return;

        if (_currentChat.MlsGroupId == null)
        {
            _logger.LogWarning("SendVoiceMessage: MLS group not available (chat type: {Type})", _currentChat.Type);
            UploadStatus = "Voice messages require an MLS group chat";
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) => { UploadStatus = null; return System.Reactive.Disposables.Disposable.Empty; });
            });
            return;
        }

        try
        {
            IsSendingVoice = true;
            IsRecording = false;
            _recordingTimerSubscription?.Dispose();
            _recordingTimerSubscription = null;

            UploadStatus = "Processing recording...";

            var recording = await AudioRecordingService.StopRecordingAsync();
            _logger.LogInformation("Recording stopped: {Duration}s, {Bytes} bytes PCM",
                recording.Duration.TotalSeconds, recording.PcmData.Length);

            UploadStatus = "Encoding audio...";

            // Encode to Opus
            var opusBytes = OpusCodec.Encode(recording.PcmData, recording.SampleRate, recording.Channels);
            var durationSeconds = recording.Duration.TotalSeconds;

            UploadStatus = "Encrypting audio...";

            // MIP-04 encrypt
            var sha256Hex = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(opusBytes)).ToLowerInvariant();
            var mediaKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(
                _mlsService.GetMediaExporterSecret(_currentChat.MlsGroupId!),
                sha256Hex, "audio/opus", "voice.opus");
            var (encrypted, nonce) = Mip04MediaCrypto.EncryptMediaFile(
                opusBytes, mediaKey, sha256Hex, "audio/opus", "voice.opus");

            // Upload to Blossom
            if (MediaUploadService == null)
                throw new InvalidOperationException("Media upload service not configured");

            UploadStatus = "Waiting for signer approval...";

            var uploadResult = await MediaUploadService.UploadAsync(encrypted, _currentUserPrivateKeyHex, "audio/opus");

            UploadStatus = "Sending message...";

            // Build imeta tag and send as MLS message
            var nonceHex = Convert.ToHexString(nonce).ToLowerInvariant();
            var imetaTag = new List<string>
            {
                "imeta",
                $"url {uploadResult.Url}",
                $"m audio/opus",
                $"x {sha256Hex}",
                $"n {nonceHex}",
                $"v mip04-v2",
                $"filename voice.opus",
                $"duration {durationSeconds:F1}"
            };

            // Send via MLS (the rumor content can be a placeholder since the media is in imeta)
            var content = $"[Voice message ({TimeSpan.FromSeconds(durationSeconds):m\\:ss})]";
            await _messageService.SendVoiceMessageAsync(
                _currentChat.Id, content, uploadResult.Url, sha256Hex, nonceHex,
                "audio/opus", "voice.opus", durationSeconds);

            _logger.LogInformation("Voice message sent: {Duration}s, {Size} bytes encrypted",
                durationSeconds, encrypted.Length);
            UploadStatus = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send voice message");
            UploadStatus = $"Voice send failed: {ex.Message}";
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) => { UploadStatus = null; return System.Reactive.Disposables.Disposable.Empty; });
            });
        }
        finally
        {
            IsSendingVoice = false;
        }
    }

    private async Task CancelRecordingAsync()
    {
        if (AudioRecordingService == null) return;

        try
        {
            _recordingTimerSubscription?.Dispose();
            _recordingTimerSubscription = null;
            await AudioRecordingService.CancelRecordingAsync();
            IsRecording = false;
            RecordingDuration = "0:00";
            _logger.LogInformation("Voice recording cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel recording");
        }
    }
}

public class MessageViewModel : ViewModelBase
{
    private static readonly ILogger<MessageViewModel> _logger = LoggingConfiguration.CreateLogger<MessageViewModel>();

    // Static service references set by ChatViewModel
    internal static IMediaDownloadService? MediaDownloadService { get; set; }
    internal static IMlsService? MlsServiceRef { get; set; }
    internal static IStorageService? StorageServiceRef { get; set; }
    internal static IMessageService? MessageServiceRef { get; set; }
    internal static Func<string, byte[]?>? MediaCacheGet { get; set; }
    internal static Action<string, byte[]>? MediaCacheSet { get; set; }

    public string Id { get; }
    public string SenderPublicKey { get; }

    [Reactive] public string SenderName { get; set; }
    [Reactive] public string? SenderAvatarUrl { get; set; }
    [Reactive] public string Content { get; set; }
    [Reactive] public DateTime Timestamp { get; set; }
    [Reactive] public bool IsFromCurrentUser { get; set; }
    [Reactive] public MessageStatus Status { get; set; }
    [Reactive] public bool IsFirstInGroup { get; set; }
    [Reactive] public bool IsLastInGroup { get; set; }

    // Media loading state (MIP-04)
    [Reactive] public bool IsLoadingMedia { get; set; }
    [Reactive] public byte[]? DecryptedMediaBytes { get; set; }
    [Reactive] public bool IsMediaLoaded { get; set; }
    [Reactive] public string? MediaError { get; set; }
    [Reactive] public string? MediaSizeDisplay { get; set; }
    [Reactive] public bool IsMip04Enabled { get; set; }

    /// <summary>
    /// True when this message is an image (MessageType.Image).
    /// </summary>
    public bool IsImage { get; }

    /// <summary>
    /// True when this message is a voice/audio message (MessageType.Audio).
    /// </summary>
    public bool IsAudio { get; }

    /// <summary>
    /// True when this message is a file attachment (MessageType.File).
    /// </summary>
    public bool IsFile { get; }

    /// <summary>
    /// True when this is a plain text message (not image, not audio, not file).
    /// </summary>
    public bool IsTextMessage => !IsImage && !IsAudio && !IsFile;

    /// <summary>
    /// Display text for image messages, e.g. "[Encrypted image: photo.jpg]".
    /// </summary>
    public string? ImageDisplayText { get; }

    /// <summary>
    /// True when the image URL points to an unknown (non-Blossom) server.
    /// </summary>
    public bool IsUnknownServer { get; }

    /// <summary>
    /// Hostname of the server for display in warnings.
    /// </summary>
    public string? ServerHostname { get; }

    // Audio playback state
    [Reactive] public bool IsPlayingAudio { get; set; }
    [Reactive] public double AudioProgress { get; set; }
    [Reactive] public string? AudioDurationText { get; set; }
    [Reactive] public string? AudioTimeDisplay { get; set; }
    [Reactive] public bool IsPausedAudio { get; set; }
    [Reactive] public byte[]? DecodedAudioPcm { get; set; }

    /// <summary>
    /// True when this is a media message and MIP-04 is disabled.
    /// </summary>
    public bool ShowMediaDisabled => IsMediaMessage && !IsMip04Enabled;

    /// <summary>
    /// True when MIP-04 is enabled, this is media, and it hasn't been loaded yet.
    /// </summary>
    public bool ShowTapToLoad => IsMediaMessage && IsMip04Enabled && !IsMediaLoaded && !IsLoadingMedia && MediaError == null;

    /// <summary>
    /// True when this message has a media attachment (image, audio, or file).
    /// </summary>
    public bool IsMediaMessage => IsImage || IsAudio || IsFile;

    // Reactions
    [Reactive] public string? ReactionsDisplay { get; set; }
    [Reactive] public bool HasReactions { get; set; }
    [Reactive] public bool IsHovering { get; set; }

    // Reply display
    public bool HasReplyTo { get; }
    public string? ReplyToSenderName { get; }
    public string? ReplyToContent { get; }

    public ReactiveCommand<string, Unit> ReactCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMediaCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAudioCommand { get; }

    public Message Message { get; }

    /// <summary>
    /// Maximum download size in bytes (50 MB).
    /// </summary>
    private const long MaxDownloadSize = 50 * 1024 * 1024;

    /// <summary>
    /// Size threshold for warning (10 MB).
    /// </summary>
    private const long LargeFileThreshold = 10 * 1024 * 1024;

    public MessageViewModel(Message message)
    {
        Message = message;
        Id = message.Id;
        SenderPublicKey = message.SenderPublicKey;
        SenderName = message.Sender?.GetDisplayNameOrNpub() ?? "Anonymous";
        SenderAvatarUrl = message.Sender?.AvatarUrl;
        Content = StripNostrMentions(message.Content);
        Timestamp = message.Timestamp;
        IsFromCurrentUser = message.IsFromCurrentUser;
        Status = message.Status;

        // Reply display
        HasReplyTo = message.ReplyToMessage != null;
        if (HasReplyTo)
        {
            var reply = message.ReplyToMessage!;
            ReplyToSenderName = reply.Sender?.GetDisplayNameOrNpub() ?? "Anonymous";
            var replyText = StripNostrMentions(reply.Content);
            ReplyToContent = replyText?.Length > 80 ? replyText[..80] + "..." : replyText;
        }

        IsImage = message.Type == MessageType.Image;
        IsAudio = message.Type == MessageType.Audio;
        IsFile = message.Type == MessageType.File;
        if (IsImage)
        {
            var displayName = !string.IsNullOrEmpty(message.FileName) ? message.FileName : "image";
            ImageDisplayText = $"[Encrypted image: {displayName}]";
        }
        else if (IsAudio)
        {
            var duration = message.AudioDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(message.AudioDurationSeconds.Value).ToString(@"m\:ss")
                : "?:??";
            ImageDisplayText = $"Voice message ({duration})";
            AudioDurationText = duration;
            AudioTimeDisplay = $"0:00 / {duration}";
        }
        else if (IsFile)
        {
            var fileName = !string.IsNullOrEmpty(message.FileName) ? message.FileName : "attachment";
            var mimeLabel = !string.IsNullOrEmpty(message.MediaType) ? message.MediaType : "file";
            ImageDisplayText = $"{fileName} ({mimeLabel})";
        }

        // Determine server safety for media messages
        if (IsMediaMessage && !string.IsNullOrEmpty(message.ImageUrl))
        {
            IsUnknownServer = MediaDownloadService != null && !MediaDownloadService.IsKnownBlossomServer(message.ImageUrl);
            if (Uri.TryCreate(message.ImageUrl, UriKind.Absolute, out var uri))
                ServerHostname = uri.Host;
        }

        // Load MIP-04 setting
        LoadMip04Setting();

        // Check cache (memory + disk)
        if ((IsImage || IsAudio) && MediaCacheGet != null)
        {
            var cached = MediaCacheGet(Id);
            if (cached != null)
            {
                DecryptedMediaBytes = cached;
                IsMediaLoaded = true;
            }
        }

        // Auto-download images and audio when MIP-04 is enabled and not yet cached
        if ((IsImage || IsAudio) && !IsMediaLoaded && IsMip04Enabled && !string.IsNullOrEmpty(message.ImageUrl))
        {
            _ = LoadMediaAsync();
        }

        UpdateReactionsDisplay();

        ReactCommand = ReactiveCommand.CreateFromTask<string>(async emoji =>
        {
            if (MessageServiceRef == null) return;
            try
            {
                await MessageServiceRef.SendReactionAsync(Id, emoji);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send reaction {Emoji} on message {Id}", emoji, Id);
            }
        });

        LoadMediaCommand = ReactiveCommand.CreateFromTask(LoadMediaAsync);
        ToggleAudioCommand = ReactiveCommand.CreateFromTask(ToggleAudioPlaybackAsync);

        // Notify computed properties when their dependencies change
        this.WhenAnyValue(x => x.IsMip04Enabled, x => x.IsMediaLoaded, x => x.IsLoadingMedia, x => x.MediaError)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(ShowTapToLoad));
                this.RaisePropertyChanged(nameof(ShowMediaDisabled));
            });
    }

    private static readonly Regex NostrMentionRegex = new(@"nostr:n[a-z]+1[a-zA-Z0-9]+", RegexOptions.Compiled);

    private static string? StripNostrMentions(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = NostrMentionRegex.Replace(text, "").Trim();
        return string.IsNullOrEmpty(result) ? text : result;
    }

    public void UpdateReactionsDisplay()
    {
        if (Message.Reactions.Count == 0)
        {
            ReactionsDisplay = null;
            HasReactions = false;
            return;
        }

        // Format: "👍 2  🔥 1"
        var parts = Message.Reactions
            .Where(r => r.Value.Count > 0)
            .Select(r => r.Value.Count > 1 ? $"{r.Key} {r.Value.Count}" : r.Key);
        ReactionsDisplay = string.Join("  ", parts);
        HasReactions = !string.IsNullOrEmpty(ReactionsDisplay);
    }

    private async void LoadMip04Setting()
    {
        if (StorageServiceRef == null) return;
        try
        {
            var setting = await StorageServiceRef.GetSettingAsync("mip04_enabled");
            IsMip04Enabled = setting == "true";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load MIP-04 setting for message {Id}", Id);
        }
    }

    private async Task LoadMediaAsync()
    {
        if ((!IsImage && !IsAudio) || string.IsNullOrEmpty(Message.ImageUrl))
        {
            MediaError = "No media URL available.";
            _logger.LogWarning("LoadMedia: no media URL for message {Id}", Id);
            return;
        }

        if (MediaDownloadService == null || MlsServiceRef == null)
        {
            MediaError = "Media services not available.";
            _logger.LogWarning("LoadMedia: services not initialized for message {Id}", Id);
            return;
        }

        if (string.IsNullOrEmpty(Message.FileSha256) || string.IsNullOrEmpty(Message.EncryptionNonce))
        {
            MediaError = "Missing encryption metadata (sha256/nonce).";
            _logger.LogWarning("LoadMedia: missing crypto fields for message {Id}", Id);
            return;
        }

        IsLoadingMedia = true;
        MediaError = null;

        try
        {
            _logger.LogInformation("LoadMedia: starting load for message {Id}, url={Url}",
                Id, Message.ImageUrl);

            // Step 1: Validate URL
            var validationError = await MediaDownloadService.ValidateUrlAsync(Message.ImageUrl);
            if (validationError != null)
            {
                MediaError = validationError;
                _logger.LogWarning("LoadMedia: URL validation failed: {Error}", validationError);
                return;
            }

            // Step 2: HEAD request to get size
            var fileInfo = await MediaDownloadService.GetFileInfoAsync(Message.ImageUrl);
            MediaSizeDisplay = fileInfo.SizeDisplay;

            if (fileInfo.Size.HasValue && fileInfo.Size.Value > MaxDownloadSize)
            {
                MediaError = $"File too large ({fileInfo.SizeDisplay}). Maximum is {MaxDownloadSize / (1024 * 1024)} MB.";
                _logger.LogWarning("LoadMedia: file too large: {Size}", fileInfo.SizeDisplay);
                return;
            }

            if (fileInfo.Size.HasValue && fileInfo.Size.Value > LargeFileThreshold)
            {
                _logger.LogWarning("LoadMedia: large file detected ({Size}), proceeding anyway", fileInfo.SizeDisplay);
            }

            // Step 3: Download
            MediaSizeDisplay = $"Downloading {fileInfo.SizeDisplay}...";
            var encrypted = await MediaDownloadService.DownloadAsync(Message.ImageUrl, MaxDownloadSize);

            // Step 4: Find the MLS group for this chat to get the exporter secret
            var chat = await StorageServiceRef!.GetChatAsync(Message.ChatId);
            if (chat?.MlsGroupId == null)
            {
                MediaError = "Cannot decrypt: no MLS group found for this chat.";
                _logger.LogWarning("LoadMedia: no MLS group for chat {ChatId}", Message.ChatId);
                return;
            }

            // Step 5: Derive key and decrypt
            MediaSizeDisplay = "Decrypting...";
            var exporterSecret = MlsServiceRef.GetMediaExporterSecret(chat.MlsGroupId);
            var mimeType = Message.MediaType ?? "application/octet-stream";
            var filename = Message.FileName ?? "unknown";

            var fileKey = OpenChat.Core.Crypto.Mip04MediaCrypto.DeriveMediaEncryptionKey(
                exporterSecret, Message.FileSha256, mimeType, filename);

            var decrypted = OpenChat.Core.Crypto.Mip04MediaCrypto.DecryptMediaFile(
                encrypted, fileKey, Message.FileSha256, mimeType, filename, Message.EncryptionNonce);

            // Step 6: Validate magic bytes (skip for audio — no image header expected)
            if (!mimeType.StartsWith("audio/"))
            {
                if (!ValidateImageMagicBytes(decrypted, mimeType))
                {
                    MediaError = "File content does not match expected image format.";
                    _logger.LogWarning("LoadMedia: magic bytes mismatch for {Id}", Id);
                    return;
                }
            }

            // Step 7: Success
            DecryptedMediaBytes = decrypted;
            IsMediaLoaded = true;
            MediaSizeDisplay = fileInfo.SizeDisplay;

            // Step 8: For audio, decode Opus → PCM and store for playback
            if (IsAudio)
            {
                try
                {
                    var (pcm, sampleRate, channels) = OpenChat.Core.Audio.OpusCodec.Decode(decrypted);
                    DecodedAudioPcm = pcm;
                    _decodedSampleRate = sampleRate;
                    _decodedChannels = channels;
                    var totalSeconds = (double)pcm.Length / (sampleRate * channels * 2); // 16-bit samples
                    var durationStr = TimeSpan.FromSeconds(totalSeconds).ToString(@"m\:ss");
                    AudioDurationText = durationStr;
                    AudioTimeDisplay = $"0:00 / {durationStr}";
                    _logger.LogInformation("LoadMedia: decoded audio for message {Id} ({Size} bytes Opus → {PcmSize} bytes PCM, {Duration}s)",
                        Id, decrypted.Length, pcm.Length, totalSeconds);

                    // Audio is decoded and ready — user taps play to start
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LoadMedia: audio decode failed for {Id}", Id);
                    MediaError = $"Audio decode failed: {ex.Message}";
                }
            }

            // Cache
            MediaCacheSet?.Invoke(Id, decrypted);

            _logger.LogInformation("LoadMedia: successfully loaded {Size} bytes for message {Id}",
                decrypted.Length, Id);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            MediaError = $"Decryption failed: {ex.Message}";
            _logger.LogError(ex, "LoadMedia: decryption failed for message {Id}", Id);
        }
        catch (Exception ex)
        {
            MediaError = $"Failed to load: {ex.Message}";
            _logger.LogError(ex, "LoadMedia: failed for message {Id}", Id);
        }
        finally
        {
            IsLoadingMedia = false;
        }
    }

    /// <summary>
    /// Validates that decrypted bytes match the expected image MIME type.
    /// Rejects SVGs to prevent script injection.
    /// </summary>
    // Audio playback state
    private int _decodedSampleRate = 48000;
    private int _decodedChannels = 1;
    private IDisposable? _audioProgressTimer;
    private bool _isUserSeeking;
    private static MessageViewModel? _currentlyPlayingMessage;

    private async Task ToggleAudioPlaybackAsync()
    {
        if (DecodedAudioPcm == null) return;

        var playback = ChatViewModel.AudioPlaybackService;
        if (playback == null) return;

        if (IsPlayingAudio)
        {
            await playback.PauseAsync();
            IsPlayingAudio = false;
            IsPausedAudio = true;
        }
        else if (IsPausedAudio)
        {
            await playback.ResumeAsync();
            IsPlayingAudio = true;
            IsPausedAudio = false;
        }
        else
        {
            await PlayAudioAsync();
        }
    }

    private async Task PlayAudioAsync()
    {
        if (DecodedAudioPcm == null) return;

        var playback = ChatViewModel.AudioPlaybackService;
        if (playback == null) return;

        // Stop any other playing message
        if (_currentlyPlayingMessage != null && _currentlyPlayingMessage != this)
        {
            _currentlyPlayingMessage.IsPlayingAudio = false;
            _currentlyPlayingMessage.IsPausedAudio = false;
            _currentlyPlayingMessage.AudioProgress = 0;
            _currentlyPlayingMessage._audioProgressTimer?.Dispose();
            _currentlyPlayingMessage._audioProgressTimer = null;
        }
        _currentlyPlayingMessage = this;

        IsPlayingAudio = true;
        IsPausedAudio = false;
        AudioProgress = 0;

        await playback.PlayAsync(DecodedAudioPcm, _decodedSampleRate, _decodedChannels);

        // Update progress every 100ms
        _audioProgressTimer?.Dispose();
        _audioProgressTimer = System.Reactive.Linq.Observable
            .Interval(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (playback.IsPlaying && playback.Duration.TotalSeconds > 0)
                {
                    if (!_isUserSeeking)
                    {
                        AudioProgress = playback.Position.TotalSeconds / playback.Duration.TotalSeconds;
                    }
                    var pos = playback.Position;
                    var dur = playback.Duration;
                    AudioTimeDisplay = $"{pos:m\\:ss} / {dur:m\\:ss}";
                }
                else if (!playback.IsPlaying && IsPlayingAudio)
                {
                    // Playback finished
                    IsPlayingAudio = false;
                    IsPausedAudio = false;
                    AudioProgress = 0;
                    AudioTimeDisplay = $"0:00 / {AudioDurationText}";
                    _audioProgressTimer?.Dispose();
                    _audioProgressTimer = null;
                }
            });
    }

    public void OnSeekStarted()
    {
        _isUserSeeking = true;
    }

    public async Task OnSeekCompleted()
    {
        _isUserSeeking = false;
        var playback = ChatViewModel.AudioPlaybackService;
        if (playback == null || playback.Duration.TotalSeconds <= 0) return;

        var targetPosition = TimeSpan.FromSeconds(AudioProgress * playback.Duration.TotalSeconds);
        await playback.SeekTo(targetPosition);
    }

    private static bool ValidateImageMagicBytes(byte[] data, string mimeType)
    {
        if (data.Length < 4) return false;

        var mime = mimeType.ToLowerInvariant();

        // Block SVGs (potential script injection)
        if (mime.Contains("svg"))
        {
            _logger.LogWarning("ValidateImageMagicBytes: SVG files are blocked for security");
            return false;
        }

        // JPEG: FF D8 FF
        if (mime.Contains("jpeg") || mime.Contains("jpg"))
            return data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

        // PNG: 89 50 4E 47
        if (mime.Contains("png"))
            return data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

        // GIF: 47 49 46 38
        if (mime.Contains("gif"))
            return data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38;

        // WebP: 52 49 46 46 ... 57 45 42 50
        if (mime.Contains("webp"))
            return data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;

        // For other types, allow if mime starts with "image/"
        if (mime.StartsWith("image/"))
            return true;

        _logger.LogWarning("ValidateImageMagicBytes: unknown MIME type {MimeType}", mimeType);
        return false;
    }
}

public class GroupMemberViewModel : ViewModelBase
{
    public string PublicKeyHex { get; set; } = string.Empty;
    [Reactive] public string DisplayName { get; set; } = string.Empty;
    [Reactive] public string? Picture { get; set; }
    [Reactive] public string? Npub { get; set; }
    [Reactive] public bool IsAdmin { get; set; }
    [Reactive] public bool IsCurrentUser { get; set; }

    /// <summary>Short display: first initial for avatar placeholder.</summary>
    public string Initial => !string.IsNullOrEmpty(DisplayName) ? DisplayName[..1].ToUpperInvariant() : "?";
}
