using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

public class MessageService : IMessageService, IDisposable
{
    private readonly ILogger<MessageService> _logger;
    private readonly IStorageService _storageService;
    private readonly INostrService _nostrService;
    private readonly IMlsService _mlsService;

    private readonly Subject<Message> _newMessages = new();
    private readonly Subject<(string MessageId, MessageStatus Status)> _messageStatusUpdates = new();
    private readonly Subject<Chat> _chatUpdates = new();
    private readonly Subject<PendingInvite> _newInvites = new();
    private readonly Subject<MlsDecryptionError> _decryptionErrors = new();

    private User? _currentUser;
    private IDisposable? _eventSubscription;
    private bool _disposed;

    public IObservable<Message> NewMessages => _newMessages.AsObservable();
    public IObservable<(string MessageId, MessageStatus Status)> MessageStatusUpdates => _messageStatusUpdates.AsObservable();
    public IObservable<Chat> ChatUpdates => _chatUpdates.AsObservable();
    public IObservable<PendingInvite> NewInvites => _newInvites.AsObservable();
    public IObservable<MlsDecryptionError> DecryptionErrors => _decryptionErrors.AsObservable();

    public MessageService(IStorageService storageService, INostrService nostrService, IMlsService mlsService)
    {
        _logger = LoggingConfiguration.CreateLogger<MessageService>();
        _storageService = storageService;
        _nostrService = nostrService;
        _mlsService = mlsService;
    }

    public async Task InitializeAsync()
    {
        await _storageService.InitializeAsync();

        _currentUser = await _storageService.GetCurrentUserAsync();

        if (_currentUser != null && !string.IsNullOrEmpty(_currentUser.PrivateKeyHex))
        {
            await _mlsService.InitializeAsync(_currentUser.PrivateKeyHex, _currentUser.PublicKeyHex);

            // Subscribe to Nostr events (kind 444 Welcome, etc.)
            _eventSubscription = _nostrService.Events.Subscribe(OnNostrEventReceived);

            _logger.LogInformation("MessageService initialized for {PubKey}",
                _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);
        }
    }

    public async Task<IEnumerable<Chat>> GetChatsAsync()
    {
        var chats = await _storageService.GetAllChatsAsync();

        // Load last message for each chat
        foreach (var chat in chats)
        {
            var messages = await _storageService.GetMessagesForChatAsync(chat.Id, 1, 0);
            chat.LastMessage = messages.FirstOrDefault();
        }

        return chats;
    }

    public async Task<Chat?> GetChatAsync(string chatId)
    {
        return await _storageService.GetChatAsync(chatId);
    }

    public async Task<IEnumerable<Message>> GetMessagesAsync(string chatId, int limit = 50, int offset = 0)
    {
        var messages = await _storageService.GetMessagesForChatAsync(chatId, limit, offset);

        // Populate sender info
        foreach (var message in messages)
        {
            message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);
            message.IsFromCurrentUser = message.SenderPublicKey == _currentUser?.PublicKeyHex;
        }

        return messages;
    }

    public async Task<Message> SendMessageAsync(string chatId, string content)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chatId,
            SenderPublicKey = _currentUser.PublicKeyHex,
            Sender = _currentUser,
            Type = MessageType.Text,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        // Save locally first
        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            // Encrypt and send via MLS if group chat
            if (chat.MlsGroupId != null)
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                _logger.LogInformation("SendMessage: encrypting for group {GroupId}, content length={Len}",
                    groupIdHex[..Math.Min(16, groupIdHex.Length)], content.Length);

                var encryptedData = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, content);
                await PersistMlsGroupStateAsync(chat.MlsGroupId);
                _logger.LogDebug("SendMessage: encrypted to {Len} bytes, publishing kind 445", encryptedData.Length);

                message.NostrEventId = await _nostrService.PublishGroupMessageAsync(
                    encryptedData, groupIdHex, _currentUser.PrivateKeyHex!);
                _logger.LogInformation("SendMessage: published kind 445 event {EventId}", message.NostrEventId);
            }
            else
            {
                // Direct message - use NIP-44
                var recipientPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(recipientPublicKey))
                {
                    _logger.LogInformation("SendMessage: encrypting DM for {Recipient}",
                        recipientPublicKey[..Math.Min(16, recipientPublicKey.Length)]);
                    var encryptedData = _nostrService.EncryptNip44(
                        content, _currentUser.PrivateKeyHex!, recipientPublicKey);
                    message.EncryptedContent = Convert.ToBase64String(encryptedData);
                }
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMessage: failed to encrypt/publish message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        // Update chat last activity
        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return message;
    }

    public async Task<Message> SendReplyAsync(string chatId, string content, string replyToMessageId)
    {
        var message = await SendMessageAsync(chatId, content);
        message.ReplyToMessageId = replyToMessageId;
        message.ReplyToMessage = await _storageService.GetMessageAsync(replyToMessageId);
        await _storageService.SaveMessageAsync(message);
        return message;
    }

    public async Task<Chat> CreateGroupAsync(string name, IEnumerable<string> memberPublicKeys)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        _logger.LogInformation("CreateGroup: creating group '{Name}' with {MemberCount} initial members",
            name, memberPublicKeys.Count());

        // Create MLS group
        var groupInfo = await _mlsService.CreateGroupAsync(name);
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        _logger.LogInformation("CreateGroup: MLS group created {GroupId}, epoch={Epoch}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], groupInfo.Epoch);

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _logger.LogInformation("CreateGroup: saved chat {ChatId} for group '{Name}'", chat.Id, name);

        // Add members
        foreach (var memberPublicKey in memberPublicKeys)
        {
            if (memberPublicKey != _currentUser.PublicKeyHex)
            {
                await AddMemberAsync(chat.Id, memberPublicKey);
            }
        }

        _chatUpdates.OnNext(chat);
        return chat;
    }

    public async Task<Chat> GetOrCreateDirectMessageAsync(string recipientPublicKey)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        // Check if DM chat already exists
        var chats = await _storageService.GetAllChatsAsync();
        var existingChat = chats.FirstOrDefault(c =>
            c.Type == ChatType.DirectMessage &&
            c.ParticipantPublicKeys.Contains(recipientPublicKey));

        if (existingChat != null)
            return existingChat;

        // Get recipient user info
        var recipient = await _storageService.GetUserByPublicKeyAsync(recipientPublicKey);
        var chatName = recipient?.GetDisplayNameOrNpub() ?? $"{recipientPublicKey[..12]}...";

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.DirectMessage,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, recipientPublicKey },
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return chat;
    }

    public async Task AddMemberAsync(string chatId, string memberPublicKey)
    {
        _logger.LogInformation("AddMember: adding {Member} to chat {ChatId}",
            memberPublicKey[..Math.Min(16, memberPublicKey.Length)], chatId);

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot add member to non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();

        // Fetch member's KeyPackage
        _logger.LogDebug("AddMember: fetching KeyPackages for {Member}", memberPublicKey[..Math.Min(16, memberPublicKey.Length)]);
        var keyPackages = await _nostrService.FetchKeyPackagesAsync(memberPublicKey);
        var keyPackage = keyPackages.FirstOrDefault()
            ?? throw new InvalidOperationException("No KeyPackage found for member");
        _logger.LogInformation("AddMember: found KeyPackage {KpId}, {Len} bytes",
            keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none",
            keyPackage.Data.Length);

        // Add to MLS group and get Welcome
        var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);
        await PersistMlsGroupStateAsync(chat.MlsGroupId);
        _logger.LogInformation("AddMember: MLS add succeeded, welcome={WelcomeLen} bytes, commit={CommitLen} bytes",
            welcome.WelcomeData.Length, welcome.CommitData?.Length ?? 0);

        // Publish Welcome message
        if (_currentUser?.PrivateKeyHex != null)
        {
            var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                welcome.WelcomeData, welcome.RecipientPublicKey, _currentUser.PrivateKeyHex);
            _logger.LogInformation("AddMember: published kind 444 Welcome event {EventId} for {Recipient}",
                welcomeEventId, welcome.RecipientPublicKey[..Math.Min(16, welcome.RecipientPublicKey.Length)]);
        }

        // Update chat participants
        if (!chat.ParticipantPublicKeys.Contains(memberPublicKey))
        {
            chat.ParticipantPublicKeys.Add(memberPublicKey);
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
            _logger.LogInformation("AddMember: updated participants for group {GroupId}, now {Count} members",
                groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.ParticipantPublicKeys.Count);
        }
    }

    public async Task RemoveMemberAsync(string chatId, string memberPublicKey)
    {
        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot remove member from non-MLS chat");

        // Remove from MLS group
        var commitData = await _mlsService.RemoveMemberAsync(chat.MlsGroupId, memberPublicKey);
        await PersistMlsGroupStateAsync(chat.MlsGroupId);

        // Publish commit
        if (_currentUser?.PrivateKeyHex != null)
        {
            var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
            await _nostrService.PublishGroupMessageAsync(commitData, groupIdHex, _currentUser.PrivateKeyHex);
        }

        // Update chat participants
        chat.ParticipantPublicKeys.Remove(memberPublicKey);
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task LeaveGroupAsync(string chatId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        await RemoveMemberAsync(chatId, _currentUser.PublicKeyHex);
    }

    public async Task MarkAsReadAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.UnreadCount = 0;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    public async Task AddReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message != null)
        {
            if (!message.Reactions.ContainsKey(emoji))
            {
                message.Reactions[emoji] = new List<string>();
            }

            if (!message.Reactions[emoji].Contains(_currentUser.PublicKeyHex))
            {
                message.Reactions[emoji].Add(_currentUser.PublicKeyHex);
                await _storageService.SaveMessageAsync(message);
            }
        }
    }

    public async Task RemoveReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message != null && message.Reactions.ContainsKey(emoji))
        {
            message.Reactions[emoji].Remove(_currentUser.PublicKeyHex);
            if (message.Reactions[emoji].Count == 0)
            {
                message.Reactions.Remove(emoji);
            }
            await _storageService.SaveMessageAsync(message);
        }
    }

    public async Task DeleteMessageAsync(string messageId)
    {
        await _storageService.DeleteMessageAsync(messageId);
    }

    public async Task ArchiveChatAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.IsArchived = true;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    public async Task SetMutedAsync(string chatId, bool muted)
    {
        var chat = await _storageService.GetChatAsync(chatId);
        if (chat != null)
        {
            chat.IsMuted = muted;
            await _storageService.SaveChatAsync(chat);
            _chatUpdates.OnNext(chat);
        }
    }

    private async Task PersistMlsGroupStateAsync(byte[] groupId)
    {
        try
        {
            var hex = Convert.ToHexString(groupId).ToLowerInvariant();
            var state = await _mlsService.ExportGroupStateAsync(groupId);
            await _storageService.SaveMlsStateAsync(hex, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist MLS state for group");
        }
    }

    private async void OnNostrEventReceived(NostrEventReceived nostrEvent)
    {
        try
        {
            _logger.LogDebug("OnNostrEventReceived: kind {Kind} event {EventId} from {Relay}",
                nostrEvent.Kind, nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)], nostrEvent.RelayUrl);

            switch (nostrEvent.Kind)
            {
                case 443: // KeyPackage
                    await HandleKeyPackageEventAsync(nostrEvent);
                    break;
                case 444: // Welcome
                    await HandleWelcomeEventAsync(nostrEvent);
                    break;
                case 445: // Group message
                    await HandleGroupMessageEventAsync(nostrEvent);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Nostr event kind {Kind}", nostrEvent.Kind);
        }
    }

    private async Task HandleKeyPackageEventAsync(NostrEventReceived nostrEvent)
    {
        var keyPackageData = Convert.FromBase64String(nostrEvent.Content);
        var keyPackage = KeyPackage.Create(nostrEvent.PublicKey, keyPackageData);
        keyPackage.NostrEventId = nostrEvent.EventId;
        await _storageService.SaveKeyPackageAsync(keyPackage);
    }

    private async Task HandleWelcomeEventAsync(NostrEventReceived nostrEvent)
    {
        _logger.LogInformation("HandleWelcomeEventAsync: processing kind 444 event {EventId} from {Sender}",
            nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        // Check if this welcome was previously accepted or declined
        if (await _storageService.IsWelcomeEventDismissedAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("Skipping dismissed Welcome event {EventId}", nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Check for duplicate by NostrEventId
        var existingInvites = await _storageService.GetPendingInvitesAsync();
        if (existingInvites.Any(i => i.NostrEventId == nostrEvent.EventId))
        {
            _logger.LogDebug("Skipping duplicate Welcome event {EventId}", nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        var welcomeData = Convert.FromBase64String(nostrEvent.Content);

        // Extract group ID from tags (h or g tag per MIP-02)
        var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && (t[0] == "h" || t[0] == "g"));
        var groupId = groupTag?[1];

        // Extract KeyPackage event ID from e tag
        var eTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "e");
        var keyPackageEventId = eTag?[1];

        // Try to fetch sender display name
        string? senderDisplayName = null;
        try
        {
            var metadata = await _nostrService.FetchUserMetadataAsync(nostrEvent.PublicKey);
            senderDisplayName = metadata?.DisplayName ?? metadata?.Name;
        }
        catch
        {
            // Not critical — display name is optional
        }

        var invite = new PendingInvite
        {
            Id = Guid.NewGuid().ToString(),
            SenderPublicKey = nostrEvent.PublicKey,
            GroupId = groupId,
            WelcomeData = welcomeData,
            KeyPackageEventId = keyPackageEventId,
            NostrEventId = nostrEvent.EventId,
            ReceivedAt = DateTime.UtcNow,
            SenderDisplayName = senderDisplayName
        };

        await _storageService.SavePendingInviteAsync(invite);
        _logger.LogInformation("Saved pending invite {InviteId} from {Sender}", invite.Id, nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);
        _newInvites.OnNext(invite);
    }

    private async Task HandleGroupMessageEventAsync(NostrEventReceived nostrEvent)
    {
        // Find the group tag (MIP-03 uses 'h', legacy uses 'g')
        var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "h")
                    ?? nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && t[0] == "g");
        if (groupTag == null)
        {
            _logger.LogDebug("HandleGroupMessage: no 'h' or 'g' tag in kind 445 event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        var groupIdHex = groupTag[1];
        var groupId = Convert.FromHexString(groupIdHex);
        _logger.LogDebug("HandleGroupMessage: kind 445 for group {GroupId} from {Sender}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)],
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        // Find chat with this group ID
        var chats = await _storageService.GetAllChatsAsync();
        var chat = chats.FirstOrDefault(c =>
            c.MlsGroupId != null &&
            Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase));

        if (chat == null)
        {
            _logger.LogWarning("HandleGroupMessage: no chat found for group {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            return;
        }

        // Skip relay echoes of our own messages (already saved locally by SendMessageAsync)
        if (nostrEvent.PublicKey == _currentUser?.PublicKeyHex)
        {
            _logger.LogDebug("HandleGroupMessage: skipping echo of own message {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Deduplicate: skip events already processed and saved to DB
        if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
            await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("HandleGroupMessage: skipping duplicate event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Decrypt message
        var encryptedData = Convert.FromBase64String(nostrEvent.Content);
        _logger.LogDebug("HandleGroupMessage: decrypting {Len} bytes for chat {ChatId}", encryptedData.Length, chat.Id);

        MlsDecryptedMessage decrypted;
        try
        {
            decrypted = await _mlsService.DecryptMessageAsync(groupId, encryptedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleGroupMessage: MLS decrypt failed for event {EventId} in group {GroupId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)],
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            _decryptionErrors.OnNext(new MlsDecryptionError
            {
                ChatId = chat.Id,
                ChatName = chat.Name,
                EventId = nostrEvent.EventId,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            });
            return;
        }

        await PersistMlsGroupStateAsync(groupId);
        _logger.LogInformation("HandleGroupMessage: decrypted message from {Sender}, epoch={Epoch}, content length={Len}",
            decrypted.SenderPublicKey[..Math.Min(16, decrypted.SenderPublicKey.Length)], decrypted.Epoch, decrypted.Plaintext.Length);

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chat.Id,
            SenderPublicKey = decrypted.SenderPublicKey,
            Type = MessageType.Text,
            Content = decrypted.Plaintext,
            NostrEventId = nostrEvent.EventId,
            MlsEpoch = decrypted.Epoch,
            Timestamp = nostrEvent.CreatedAt,
            ReceivedAt = DateTime.UtcNow,
            Status = MessageStatus.Delivered,
            IsFromCurrentUser = decrypted.SenderPublicKey == _currentUser?.PublicKeyHex
        };

        message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        // Update chat
        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        if (!message.IsFromCurrentUser)
        {
            chat.UnreadCount++;
        }
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task<IEnumerable<PendingInvite>> GetPendingInvitesAsync()
    {
        return await _storageService.GetPendingInvitesAsync();
    }

    public async Task<Chat> AcceptInviteAsync(string inviteId)
    {
        _logger.LogInformation("Accepting invite {InviteId}", inviteId);

        var invites = await _storageService.GetPendingInvitesAsync();
        var invite = invites.FirstOrDefault(i => i.Id == inviteId)
            ?? throw new ArgumentException("Invite not found", nameof(inviteId));

        // Process MLS welcome — pass both welcome data and the kind-444 wrapper event ID
        _logger.LogInformation("AcceptInvite: processing welcome with wrapperEventId={EventId}",
            invite.NostrEventId[..Math.Min(16, invite.NostrEventId.Length)]);
        var groupInfo = await _mlsService.ProcessWelcomeAsync(invite.WelcomeData, invite.NostrEventId);
        await PersistMlsGroupStateAsync(groupInfo.GroupId);

        // Create chat for the group — fall back to sender name if the MLS Welcome
        // didn't carry a group name (e.g., Rust-originated or older welcomes)
        var chatName = !string.IsNullOrWhiteSpace(groupInfo.GroupName)
            ? groupInfo.GroupName
            : !string.IsNullOrWhiteSpace(invite.SenderDisplayName)
                ? $"Chat with {invite.SenderDisplayName}"
                : $"Group {invite.SenderPublicKey[..8]}";

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            MlsEpoch = groupInfo.Epoch,
            ParticipantPublicKeys = groupInfo.MemberPublicKeys,
            WelcomeNostrEventId = invite.NostrEventId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
        await _storageService.DeletePendingInviteAsync(inviteId);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("Accepted invite {InviteId}, created chat {ChatId} for group {GroupName}", inviteId, chat.Id, chat.Name);
        return chat;
    }

    public async Task DeclineInviteAsync(string inviteId)
    {
        _logger.LogInformation("Declining invite {InviteId}", inviteId);

        var invites = await _storageService.GetPendingInvitesAsync();
        var invite = invites.FirstOrDefault(i => i.Id == inviteId);
        if (invite != null)
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);

        await _storageService.DeletePendingInviteAsync(inviteId);
    }

    public async Task RescanInvitesAsync()
    {
        if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
        {
            _logger.LogWarning("Cannot rescan invites: no current user");
            return;
        }

        _logger.LogInformation("Rescanning for invites for {PubKey}...",
            _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);

        // Directly fetch kind 444 events from each relay (opens fresh connections)
        var welcomeEvents = await _nostrService.FetchWelcomeEventsAsync(_currentUser.PublicKeyHex);

        var newCount = 0;
        foreach (var nostrEvent in welcomeEvents)
        {
            try
            {
                // Check if this welcome was previously accepted or declined
                if (await _storageService.IsWelcomeEventDismissedAsync(nostrEvent.EventId))
                {
                    _logger.LogDebug("Skipping dismissed Welcome event {EventId}", nostrEvent.EventId);
                    continue;
                }

                // Check for duplicate by NostrEventId
                var existingInvites = await _storageService.GetPendingInvitesAsync();
                if (existingInvites.Any(i => i.NostrEventId == nostrEvent.EventId))
                {
                    _logger.LogDebug("Skipping duplicate Welcome event {EventId}", nostrEvent.EventId);
                    continue;
                }

                // Also check if we already have a chat from this welcome (previously accepted)
                var chats = await _storageService.GetAllChatsAsync();
                var groupTag = nostrEvent.Tags.FirstOrDefault(t => t.Count > 1 && (t[0] == "h" || t[0] == "g"));
                var groupIdHex = groupTag?[1];
                if (!string.IsNullOrEmpty(groupIdHex) && chats.Any(c =>
                    c.MlsGroupId != null &&
                    Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("Skipping Welcome event {EventId} — already have chat for group {GroupId}",
                        nostrEvent.EventId, groupIdHex);
                    continue;
                }

                await HandleWelcomeEventAsync(nostrEvent);
                newCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing fetched Welcome event {EventId}", nostrEvent.EventId);
            }
        }

        _logger.LogInformation("Rescan complete: {Total} Welcome event(s) found, {New} new invite(s) created",
            welcomeEvents.Count(), newCount);
    }

    public async Task ResetGroupAsync(string chatId)
    {
        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot reset a non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
        _logger.LogInformation("ResetGroup: resetting MLS state for chat {ChatId}, group {GroupId}",
            chatId, groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // 1. Delete corrupted MLS state
        await _storageService.DeleteMlsStateAsync(groupIdHex);

        // 2. Un-dismiss the welcome so rescan can find it
        if (!string.IsNullOrEmpty(chat.WelcomeNostrEventId))
            await _storageService.UndismissWelcomeEventAsync(chat.WelcomeNostrEventId);

        // 3. Delete the chat and messages
        await _storageService.DeleteChatAsync(chatId);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("ResetGroup: chat {ChatId} deleted, welcome un-dismissed", chatId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _eventSubscription?.Dispose();
        _newMessages.Dispose();
        _messageStatusUpdates.Dispose();
        _chatUpdates.Dispose();
        _newInvites.Dispose();
        _decryptionErrors.Dispose();

        _disposed = true;
    }
}
