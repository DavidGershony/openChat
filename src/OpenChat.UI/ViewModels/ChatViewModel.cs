using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.UI.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly ILogger<ChatViewModel> _logger;
    private readonly IMessageService _messageService;
    private readonly IStorageService _storageService;
    private readonly INostrService _nostrService;
    private readonly IMlsService _mlsService;
    private IDisposable? _messageSubscription;
    private Chat? _currentChat;
    private string? _currentUserPrivateKeyHex;
    private string? _currentUserPublicKeyHex;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

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

    // Invite to group
    [Reactive] public bool ShowInviteDialog { get; set; }
    [Reactive] public string InvitePublicKey { get; set; } = string.Empty;
    [Reactive] public string? InviteError { get; set; }
    [Reactive] public string? InviteSuccess { get; set; }
    [Reactive] public string? GroupInviteLink { get; set; }

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatInfoCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMetadataPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowInviteDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseInviteDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> SendInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyGroupLinkCommand { get; }

    public ChatViewModel(IMessageService messageService, IStorageService storageService, INostrService nostrService, IMlsService mlsService)
    {
        _logger = LoggingConfiguration.CreateLogger<ChatViewModel>();
        _messageService = messageService;
        _storageService = storageService;
        _nostrService = nostrService;
        _mlsService = mlsService;

        var canSend = this.WhenAnyValue(
            x => x.MessageText,
            x => x.IsSending,
            x => x.HasChat,
            (text, sending, hasChat) => !string.IsNullOrWhiteSpace(text) && !sending && hasChat);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreMessagesAsync);
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
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var clipboard = desktop.MainWindow?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(GroupInviteLink);
                        InviteSuccess = "Group ID copied to clipboard!";
                        await Task.Delay(2000);
                        if (ShowInviteDialog) InviteSuccess = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy group link");
            }
        });

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
        ShowMetadataPanel = false;
        ContactMetadata = null;
        ContactPublicKey = null;
        ClearMetadataProperties();
        Messages.Clear();
    }

    /// <summary>
    /// Sets the current user's keys for signing Nostr events and identifying self.
    /// </summary>
    public void SetUserContext(string privateKeyHex, string? publicKeyHex = null)
    {
        _currentUserPrivateKeyHex = privateKeyHex;
        _currentUserPublicKeyHex = publicKeyHex;
        _logger.LogDebug("User context set for ChatViewModel");
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

    private async Task SendInviteAsync()
    {
        if (string.IsNullOrWhiteSpace(InvitePublicKey) || _currentChat == null)
        {
            InviteError = "Please enter a valid public key";
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
                return;
            }

            // Check if user is already a member (check both hex and npub formats)
            if (_currentChat.ParticipantPublicKeys.Any(pk =>
                pk.Equals(pubKeyHex, StringComparison.OrdinalIgnoreCase) ||
                pk.Equals(pubKey, StringComparison.OrdinalIgnoreCase)))
            {
                InviteError = "This user is already a member of the group";
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
                        if (!string.IsNullOrEmpty(_currentUserPrivateKeyHex))
                        {
                            _logger.LogDebug("Publishing Welcome message to relays");
                            InviteSuccess = "Publishing invite to Nostr...";

                            var eventId = await _nostrService.PublishWelcomeAsync(
                                welcome.WelcomeData,
                                pubKeyHex,
                                _currentUserPrivateKeyHex);

                            _logger.LogInformation("Published Welcome message {EventId} for {PubKey}",
                                eventId, pubKeyHex[..16] + "...");
                        }
                        else
                        {
                            _logger.LogWarning("No private key set - Welcome not published to relays");
                        }
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

        if (!message.IsFromCurrentUser)
        {
            _messageService.MarkAsReadAsync(ChatId!).ConfigureAwait(false);
        }
    }
}

public class MessageViewModel : ViewModelBase
{
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

    public Message Message { get; }

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
    }
}
