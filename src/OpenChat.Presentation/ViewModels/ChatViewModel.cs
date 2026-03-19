using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
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

    // Load older messages
    [Reactive] public bool IsLoadingOlder { get; set; }
    [Reactive] public bool CanLoadOlder { get; set; } = true;

    // Invite to group
    [Reactive] public bool ShowInviteDialog { get; set; }
    [Reactive] public string InvitePublicKey { get; set; } = string.Empty;
    [Reactive] public string? InviteError { get; set; }
    [Reactive] public string? InviteSuccess { get; set; }
    [Reactive] public string? GroupInviteLink { get; set; }

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

    // Static service references for voice message flow (set by platform startup)
    public static IAudioRecordingService? AudioRecordingService { get; set; }
    public static IAudioPlaybackService? AudioPlaybackService { get; set; }
    public static IMediaUploadService? MediaUploadService { get; set; }

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
        MessageViewModel.MediaCacheGet = id => _mediaCache.TryGetValue(id, out var bytes) ? bytes : null;
        MessageViewModel.MediaCacheSet = (id, bytes) => _mediaCache[id] = bytes;

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
            (hasChat, sending) => hasChat && !sending);
        ToggleRecordingCommand = ReactiveCommand.CreateFromTask(ToggleRecordingAsync, canRecord);
        CancelRecordingCommand = ReactiveCommand.CreateFromTask(CancelRecordingAsync);

        // Subscribe to new messages
        _messageSubscription = _messageService.NewMessages
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnNewMessage);
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
        HasChat = true;

        // Reset load-older state
        _fetchBoundary = null;
        CanLoadOlder = true;

        // Reset metadata panel state
        ShowMetadataPanel = false;
        ContactMetadata = null;

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
        HasChat = false;
        _fetchBoundary = null;
        CanLoadOlder = true;
        ShowMetadataPanel = false;
        ContactMetadata = null;
        ContactPublicKey = null;
        ClearMetadataProperties();
        Messages.Clear();
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

            var metadata = await _nostrService.FetchUserMetadataAsync(pubKeyHex);
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
                // Fall back to local-only invite if no KeyPackage
                InviteSuccess = "No KeyPackage found - adding locally...";
            }
            else
            {
                // Step 2: Create MLS Welcome message using their KeyPackage
                _logger.LogDebug("Creating MLS Welcome message");
                InviteSuccess = "Creating encrypted invite...";

                if (_currentChat.MlsGroupId != null)
                {
                    try
                    {
                        var welcome = await _mlsService.AddMemberAsync(_currentChat.MlsGroupId, keyPackage);

                        // Step 3: Publish Welcome to relays (kind 444)
                        _logger.LogDebug("Publishing Welcome message to relays");
                        InviteSuccess = "Publishing invite to Nostr...";

                        var eventId = await _nostrService.PublishWelcomeAsync(
                            welcome.WelcomeData,
                            pubKeyHex,
                            _currentUserPrivateKeyHex);

                        _logger.LogInformation("Published Welcome message {EventId} for {PubKey}",
                            eventId, pubKeyHex[..16] + "...");
                    }
                    catch (Exception mlsEx)
                    {
                        _logger.LogWarning(mlsEx, "MLS operation failed - falling back to local invite");
                    }
                }
            }

            // Step 4: Add to local participants list
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

    private async Task SendMessageAsync()
    {
        if (ChatId == null || string.IsNullOrWhiteSpace(MessageText)) return;

        IsSending = true;

        try
        {
            var text = MessageText;
            MessageText = string.Empty;

            await _messageService.SendMessageAsync(ChatId, text);
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

        try
        {
            IsSendingVoice = true;
            IsRecording = false;

            var recording = await AudioRecordingService.StopRecordingAsync();
            _logger.LogInformation("Recording stopped: {Duration}s, {Bytes} bytes PCM",
                recording.Duration.TotalSeconds, recording.PcmData.Length);

            // Encode to Opus
            var opusBytes = OpusCodec.Encode(recording.PcmData, recording.SampleRate, recording.Channels);
            var durationSeconds = recording.Duration.TotalSeconds;

            // MIP-04 encrypt
            var sha256Hex = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(opusBytes)).ToLowerInvariant();
            var mediaKey = Mip04MediaCrypto.DeriveMediaEncryptionKey(
                _mlsService.GetMediaExporterSecret(_currentChat.MlsGroupId!),
                sha256Hex, "audio/opus", "voice.opus");
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var encrypted = Mip04MediaCrypto.EncryptMediaFile(
                opusBytes, mediaKey, sha256Hex, "audio/opus", "voice.opus", nonce);

            // Upload to Blossom
            if (MediaUploadService == null)
                throw new InvalidOperationException("Media upload service not configured");

            var uploadResult = await MediaUploadService.UploadAsync(encrypted, _currentUserPrivateKeyHex);

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send voice message");
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
            await AudioRecordingService.CancelRecordingAsync();
            IsRecording = false;
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
    /// True when this is a plain text message (not image, not audio).
    /// </summary>
    public bool IsTextMessage => !IsImage && !IsAudio;

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
    [Reactive] public byte[]? DecodedAudioPcm { get; set; }

    /// <summary>
    /// True when this is a media message (image or audio) and MIP-04 is disabled.
    /// </summary>
    public bool ShowMediaDisabled => (IsImage || IsAudio) && !IsMip04Enabled;

    /// <summary>
    /// True when MIP-04 is enabled, this is media, and it hasn't been loaded yet.
    /// </summary>
    public bool ShowTapToLoad => (IsImage || IsAudio) && IsMip04Enabled && !IsMediaLoaded && !IsLoadingMedia && MediaError == null;

    public ReactiveCommand<Unit, Unit> LoadMediaCommand { get; }

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
        SenderName = message.Sender?.GetDisplayNameOrNpub() ?? message.SenderPublicKey[..12] + "...";
        SenderAvatarUrl = message.Sender?.AvatarUrl;
        Content = message.Content;
        Timestamp = message.Timestamp;
        IsFromCurrentUser = message.IsFromCurrentUser;
        Status = message.Status;

        IsImage = message.Type == MessageType.Image;
        IsAudio = message.Type == MessageType.Audio;
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
        }

        // Determine server safety for media messages
        if ((IsImage || IsAudio) && !string.IsNullOrEmpty(message.ImageUrl))
        {
            IsUnknownServer = MediaDownloadService != null && !MediaDownloadService.IsKnownBlossomServer(message.ImageUrl);
            if (Uri.TryCreate(message.ImageUrl, UriKind.Absolute, out var uri))
                ServerHostname = uri.Host;
        }

        // Load MIP-04 setting
        LoadMip04Setting();

        // Check cache
        if ((IsImage || IsAudio) && MediaCacheGet != null)
        {
            var cached = MediaCacheGet(Id);
            if (cached != null)
            {
                DecryptedMediaBytes = cached;
                IsMediaLoaded = true;
            }
        }

        LoadMediaCommand = ReactiveCommand.CreateFromTask(LoadMediaAsync);
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

            // Step 6: Validate image magic bytes
            if (!ValidateImageMagicBytes(decrypted, mimeType))
            {
                MediaError = "File content does not match expected image format.";
                _logger.LogWarning("LoadMedia: magic bytes mismatch for {Id}", Id);
                return;
            }

            // Step 7: Success
            DecryptedMediaBytes = decrypted;
            IsMediaLoaded = true;
            MediaSizeDisplay = fileInfo.SizeDisplay;

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
