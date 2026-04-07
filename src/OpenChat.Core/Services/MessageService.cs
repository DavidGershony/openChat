using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Crypto;
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
    private readonly Subject<(string MessageId, string Emoji, string ReactorPublicKey)> _reactionUpdates = new();

    private User? _currentUser;
    private IDisposable? _eventSubscription;
    private bool _disposed;

    public IObservable<Message> NewMessages => _newMessages.AsObservable();
    public IObservable<(string MessageId, MessageStatus Status)> MessageStatusUpdates => _messageStatusUpdates.AsObservable();
    public IObservable<Chat> ChatUpdates => _chatUpdates.AsObservable();
    public IObservable<PendingInvite> NewInvites => _newInvites.AsObservable();
    public IObservable<MlsDecryptionError> DecryptionErrors => _decryptionErrors.AsObservable();
    public IObservable<(string MessageId, string Emoji, string ReactorPublicKey)> ReactionUpdates => _reactionUpdates.AsObservable();

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

        if (_currentUser != null && !string.IsNullOrEmpty(_currentUser.PublicKeyHex))
        {
            try
            {
                var mlsPrivateKey = _currentUser.PrivateKeyHex ?? new string('0', 64);
                await _mlsService.InitializeAsync(mlsPrivateKey, _currentUser.PublicKeyHex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLS initialization failed in MessageService. MLS operations will be unavailable.");
            }

            // Subscribe to Nostr events (kind 444 Welcome, etc.)
            // Dispose any previous subscription to prevent duplicate event processing
            // (e.g., if InitializeAsync is called more than once)
            _eventSubscription?.Dispose();
            _eventSubscription = _nostrService.Events.Subscribe(OnNostrEventReceived);

            _logger.LogInformation("MessageService initialized for {PubKey}",
                _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);
        }
    }

    public async Task<IEnumerable<Chat>> GetChatsAsync()
    {
        var chats = await _storageService.GetAllChatsAsync();

        // Batch-load last message for all chats in a single query
        var lastMessages = await _storageService.GetLastMessagePerChatAsync();
        foreach (var chat in chats)
        {
            chat.LastMessage = lastMessages.GetValueOrDefault(chat.Id);
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

        // Batch-resolve all unique senders in a single query instead of N queries
        var uniqueSenderKeys = messages.Select(m => m.SenderPublicKey).Distinct().ToList();
        var senderLookup = new Dictionary<string, User?>();
        foreach (var key in uniqueSenderKeys)
        {
            senderLookup[key] = await _storageService.GetUserByPublicKeyAsync(key);
        }

        // Build a lookup of loaded messages so reply resolution can avoid extra DB queries
        var messageList = messages.ToList();
        var messageLookup = messageList.ToDictionary(m => m.Id);

        var unknownKeys = new List<string>();
        foreach (var message in messageList)
        {
            message.Sender = senderLookup.GetValueOrDefault(message.SenderPublicKey);
            message.IsFromCurrentUser = message.SenderPublicKey == _currentUser?.PublicKeyHex;
            if (message.Sender == null && !message.IsFromCurrentUser)
            {
                unknownKeys.Add(message.SenderPublicKey);
            }

            // Resolve ReplyToMessage so the UI can show the reply quote
            if (!string.IsNullOrEmpty(message.ReplyToMessageId) && message.ReplyToMessage == null)
            {
                // Try the in-memory batch first, fall back to DB
                if (messageLookup.TryGetValue(message.ReplyToMessageId, out var replyTarget))
                {
                    message.ReplyToMessage = replyTarget;
                }
                else
                {
                    message.ReplyToMessage = await _storageService.GetMessageAsync(message.ReplyToMessageId);
                }

                // Resolve the reply target's sender for display
                if (message.ReplyToMessage != null && message.ReplyToMessage.Sender == null)
                {
                    message.ReplyToMessage.Sender = senderLookup.GetValueOrDefault(message.ReplyToMessage.SenderPublicKey)
                        ?? await _storageService.GetUserByPublicKeyAsync(message.ReplyToMessage.SenderPublicKey);
                }
            }
        }

        // Background-fetch profiles for any unknown senders (e.g. from before caching was added)
        if (unknownKeys.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var pubKey in unknownKeys.Distinct())
                {
                    try
                    {
                        await FetchAndCacheProfileAsync(pubKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background profile fetch failed for {PubKey}", pubKey[..Math.Min(16, pubKey.Length)]);
                    }
                }
            });
        }

        return messageList;
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

                // EncryptMessageAsync returns a complete signed kind 445 Nostr event JSON
                // with the correct h-tag (NostrGroupId from 0xF2EE extension).
                // Publish it directly — do NOT wrap in another event.
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, content);
                _logger.LogDebug("SendMessage: encrypted to {Len} bytes, publishing kind 445", eventJsonBytes.Length);

                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("SendMessage: published kind 445 event {EventId}, rumorId={RumorId}",
                    message.NostrEventId, message.RumorEventId?[..Math.Min(16, message.RumorEventId?.Length ?? 0)]);
            }
            else if (chat.Type == ChatType.Bot)
            {
                // Bot chat - use NIP-17 gift-wrapped DMs (kind 14 rumor in kind 1059)
                var botPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(botPublicKey))
                {
                    _logger.LogInformation("SendMessage: creating NIP-17 gift wrap for bot {Bot}",
                        botPublicKey[..Math.Min(16, botPublicKey.Length)]);

                    var rumorTags = new List<List<string>>
                    {
                        new() { "p", botPublicKey }
                    };

                    var botRelays = chat.RelayUrls.Count > 0 ? chat.RelayUrls : null;
                    message.NostrEventId = await _nostrService.PublishGiftWrapAsync(
                        rumorKind: 14,
                        content: content,
                        rumorTags: rumorTags,
                        senderPrivateKeyHex: _currentUser.PrivateKeyHex,
                        senderPublicKeyHex: _currentUser.PublicKeyHex,
                        recipientPublicKeyHex: botPublicKey,
                        targetRelayUrls: botRelays);

                    _logger.LogInformation("SendMessage: published gift-wrapped bot DM {EventId} to {Relays}",
                        message.NostrEventId[..Math.Min(16, message.NostrEventId.Length)],
                        botRelays != null ? string.Join(", ", botRelays) : "all relays");
                }
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
                    var encryptedContent = await _nostrService.Nip44EncryptAsync(content, recipientPublicKey);
                    message.EncryptedContent = encryptedContent;
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

    public async Task SendVoiceMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, double durationSeconds)
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
            Type = MessageType.Audio,
            Content = content,
            ImageUrl = mediaUrl,
            FileName = filename,
            FileSha256 = sha256Hex,
            EncryptionNonce = nonceHex,
            MediaType = mimeType,
            EncryptionVersion = "mip04-v2",
            AudioDurationSeconds = durationSeconds,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // MIP-04 v2 imeta tags for voice messages
                var imetaTags = new List<List<string>>
                {
                    new() { "imeta",
                        $"url {mediaUrl}",
                        $"m {mimeType}",
                        $"x {sha256Hex}",
                        $"n {nonceHex}",
                        $"v mip04-v2",
                        $"filename {filename}",
                        $"duration {durationSeconds:F1}" }
                };
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, "", imetaTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("Voice message published with imeta tags: event {EventId}", message.NostrEventId);
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send voice message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task SendMediaMessageAsync(string chatId, string content, string mediaUrl,
        string sha256Hex, string nonceHex, string mimeType, string filename, MessageType messageType)
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
            Type = messageType,
            Content = content,
            ImageUrl = mediaUrl,
            FileName = filename,
            FileSha256 = sha256Hex,
            EncryptionNonce = nonceHex,
            MediaType = mimeType,
            EncryptionVersion = "mip04-v2",
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Pending,
            IsFromCurrentUser = true
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // Build imeta tags for MIP-04 media metadata so cross-impl clients
                // can download and decrypt the file
                // MIP-04 v2 imeta field names: n=nonce, v=version, filename=filename
                var imetaTags = new List<List<string>>
                {
                    new() { "imeta",
                        $"url {mediaUrl}",
                        $"m {mimeType}",
                        $"x {sha256Hex}",
                        $"n {nonceHex}",
                        $"v mip04-v2",
                        $"filename {filename}" }
                };
                // Content is empty per MIP-04 — all metadata is in the imeta tag
                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, "", imetaTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
                _logger.LogInformation("Media message published with imeta tags: event {EventId}", message.NostrEventId);
            }
            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send media message for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);
    }

    public async Task<Message> SendReplyAsync(string chatId, string content, string replyToMessageId)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        var replyToMessage = await _storageService.GetMessageAsync(replyToMessageId);

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
            IsFromCurrentUser = true,
            ReplyToMessageId = replyToMessageId,
            ReplyToMessage = replyToMessage
        };

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        try
        {
            if (chat.MlsGroupId != null)
            {
                // Build reply "e" tag with "reply" marker per NIP-10.
                // Use RumorEventId if available (for targeting within MLS), otherwise NostrEventId.
                var targetEventId = replyToMessage?.RumorEventId ?? replyToMessage?.NostrEventId ?? replyToMessageId;
                var replyTags = new List<List<string>>
                {
                    new() { "e", targetEventId, "", "reply" }
                };

                _logger.LogInformation("SendReply: encrypting reply to {Target} for group, content length={Len}",
                    targetEventId[..Math.Min(16, targetEventId.Length)], content.Length);

                var eventJsonBytes = await _mlsService.EncryptMessageAsync(chat.MlsGroupId, content, replyTags);
                message.RumorEventId = _mlsService.LastEncryptedRumorEventId;
                message.NostrEventId = await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);

                _logger.LogInformation("SendReply: published kind 445 event {EventId}", message.NostrEventId);
            }
            else if (chat.Type == ChatType.Bot)
            {
                var botPublicKey = chat.ParticipantPublicKeys
                    .FirstOrDefault(pk => pk != _currentUser.PublicKeyHex);

                if (!string.IsNullOrEmpty(botPublicKey))
                {
                    var targetEventId = replyToMessage?.NostrEventId ?? replyToMessageId;
                    var rumorTags = new List<List<string>>
                    {
                        new() { "p", botPublicKey },
                        new() { "e", targetEventId, "", "reply" }
                    };

                    var botRelays = chat.RelayUrls.Count > 0 ? chat.RelayUrls : null;
                    message.NostrEventId = await _nostrService.PublishGiftWrapAsync(
                        rumorKind: 14,
                        content: content,
                        rumorTags: rumorTags,
                        senderPrivateKeyHex: _currentUser.PrivateKeyHex,
                        senderPublicKeyHex: _currentUser.PublicKeyHex,
                        recipientPublicKeyHex: botPublicKey,
                        targetRelayUrls: botRelays);
                }
            }

            message.Status = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendReply: failed to encrypt/publish reply for chat {ChatId}", chatId);
            message.Status = MessageStatus.Failed;
        }

        await _storageService.SaveMessageAsync(message);
        _messageStatusUpdates.OnNext((message.Id, message.Status));

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        return message;
    }

    public async Task<Chat> CreateGroupAsync(string name, IEnumerable<string> memberPublicKeys)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        _logger.LogInformation("CreateGroup: creating group '{Name}' with {MemberCount} initial members",
            name, memberPublicKeys.Count());

        // Create MLS group with connected relay URLs
        var relayUrls = _nostrService.ConnectedRelayUrls.ToArray();
        if (relayUrls.Length == 0)
            throw new InvalidOperationException("Cannot create group: no relays connected");
        var groupInfo = await _mlsService.CreateGroupAsync(name, relayUrls);
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

    public async Task<Chat> GetOrCreateBotChatAsync(string botPublicKey, List<string>? relayUrls = null)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        // Check if bot chat already exists
        var chats = await _storageService.GetAllChatsAsync();
        var existingChat = chats.FirstOrDefault(c =>
            c.Type == ChatType.Bot &&
            c.ParticipantPublicKeys.Contains(botPublicKey));

        if (existingChat != null)
        {
            // Update relay URLs if provided and chat had none
            if (relayUrls is { Count: > 0 } && existingChat.RelayUrls.Count == 0)
            {
                existingChat.RelayUrls = relayUrls;
                await _storageService.SaveChatAsync(existingChat);
                _logger.LogInformation("Updated relay URLs for existing bot chat {ChatId}: {Relays}",
                    existingChat.Id[..Math.Min(8, existingChat.Id.Length)], string.Join(", ", relayUrls));
            }
            return existingChat;
        }

        // Get bot profile for display name
        var botUser = await _storageService.GetUserByPublicKeyAsync(botPublicKey);
        var chatName = botUser?.GetDisplayNameOrNpub() ?? $"{botPublicKey[..12]}...";

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.Bot,
            ParticipantPublicKeys = new List<string> { _currentUser.PublicKeyHex, botPublicKey },
            RelayUrls = relayUrls ?? new List<string>(),
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("Created bot chat {ChatId} with {RelayCount} relay(s)",
            chat.Id[..Math.Min(8, chat.Id.Length)], chat.RelayUrls.Count);

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

        // Fetch member's KeyPackage — only use supported cipher suites
        _logger.LogDebug("AddMember: fetching KeyPackages for {Member}", memberPublicKey[..Math.Min(16, memberPublicKey.Length)]);
        var keyPackages = await _nostrService.FetchKeyPackagesAsync(memberPublicKey);
        var keyPackage = keyPackages.FirstOrDefault(kp => kp.IsCipherSuiteSupported)
            ?? throw new InvalidOperationException(
                keyPackages.Any()
                    ? $"No KeyPackage with a supported cipher suite found for member (found {keyPackages.Count()} with unsupported suites)"
                    : "No KeyPackage found for member");
        _logger.LogInformation("AddMember: found KeyPackage {KpId}, {Len} bytes",
            keyPackage.NostrEventId?[..Math.Min(16, keyPackage.NostrEventId?.Length ?? 0)] ?? "none",
            keyPackage.Data.Length);

        // Add to MLS group and get Welcome
        var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);
        _logger.LogInformation("AddMember: MLS add succeeded, welcome={WelcomeLen} bytes, commit={CommitLen} bytes",
            welcome.WelcomeData.Length, welcome.CommitData?.Length ?? 0);

        // Publish commit event (kind 445) for existing group members to advance their epoch.
        // Per MIP-03, the commit must be published BEFORE the welcome so existing members
        // can process it and stay in sync with the group's MLS state.
        if (_currentUser != null && welcome.CommitData != null && welcome.CommitData.Length > 0)
        {
            var nostrGroupId = _mlsService.GetNostrGroupId(chat.MlsGroupId);
            var commitGroupId = nostrGroupId != null
                ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
                : groupIdHex;
            var commitEventId = await _nostrService.PublishCommitAsync(
                welcome.CommitData, commitGroupId, _currentUser.PrivateKeyHex);
            _logger.LogInformation("AddMember: published kind 445 commit event {EventId} for group {GroupId}",
                commitEventId, commitGroupId[..Math.Min(16, commitGroupId.Length)]);
        }

        // Publish Welcome message (NIP-59 gift wrapped kind 444)
        if (_currentUser != null)
        {
            var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                welcome.WelcomeData, welcome.RecipientPublicKey, _currentUser.PrivateKeyHex);
            _logger.LogInformation("AddMember: published kind 444 Welcome event {EventId} for {Recipient}",
                welcomeEventId, welcome.RecipientPublicKey[..Math.Min(16, welcome.RecipientPublicKey.Length)]);
        }

        // Update chat participants
        if (!chat.ParticipantPublicKeys.Any(p => string.Equals(p, memberPublicKey, StringComparison.OrdinalIgnoreCase)))
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

        // Publish commit
        if (_currentUser != null)
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

        var chat = await _storageService.GetChatAsync(chatId)
            ?? throw new ArgumentException("Chat not found", nameof(chatId));

        if (chat.MlsGroupId == null)
            throw new InvalidOperationException("Cannot leave a non-MLS chat");

        var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
        _logger.LogInformation("LeaveGroup: leaving chat {ChatId}, group {GroupId}",
            chatId, groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // MLS does not allow self-removal via Commit (RFC 9420).
        // Clean up locally: delete MLS state, chat, and messages.
        // Other members will keep encrypting for us until they remove us.
        await _storageService.DeleteMlsStateAsync(groupIdHex);
        await _storageService.DeleteChatAsync(chatId);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("LeaveGroup: local state for chat {ChatId} cleaned up", chatId);
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

    public async Task SendReactionAsync(string messageId, string emoji)
    {
        if (_currentUser == null)
            throw new InvalidOperationException("User not logged in");

        var message = await _storageService.GetMessageAsync(messageId);
        if (message == null)
        {
            _logger.LogWarning("SendReaction: message {MessageId} not found", messageId);
            return;
        }

        var targetRumorId = message.RumorEventId ?? message.NostrEventId;
        if (string.IsNullOrEmpty(targetRumorId))
        {
            _logger.LogWarning("SendReaction: message {MessageId} has no event ID to target", messageId);
            return;
        }

        var chat = await _storageService.GetChatAsync(message.ChatId);
        if (chat?.MlsGroupId == null)
        {
            _logger.LogWarning("SendReaction: chat {ChatId} has no MLS group", message.ChatId);
            return;
        }

        try
        {
            var eventJsonBytes = await _mlsService.EncryptReactionAsync(chat.MlsGroupId, emoji, targetRumorId);
            await _nostrService.PublishRawEventJsonAsync(eventJsonBytes);
            _logger.LogInformation("SendReaction: published {Emoji} reaction on message {MessageId}", emoji, messageId);

            // Update local storage
            await AddReactionAsync(messageId, emoji);
            _reactionUpdates.OnNext((messageId, emoji, _currentUser.PublicKeyHex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendReaction: failed to send {Emoji} reaction on message {MessageId}", emoji, messageId);
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

    private async void OnNostrEventReceived(NostrEventReceived nostrEvent)
    {
        try
        {
            _logger.LogInformation("OnNostrEventReceived: kind {Kind} event {EventId} from {Relay}",
                nostrEvent.Kind, nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)], nostrEvent.RelayUrl);

            switch (nostrEvent.Kind)
            {
                case 14: // NIP-17 DM (bot chat)
                    await HandleBotMessageEventAsync(nostrEvent);
                    break;
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

    private async Task HandleBotMessageEventAsync(NostrEventReceived nostrEvent)
    {
        _logger.LogInformation("HandleBotMessage: kind 14 DM from {Sender}",
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        if (_currentUser == null) return;

        var senderPubKey = nostrEvent.PublicKey;

        // Skip self-echoes
        if (senderPubKey == _currentUser.PublicKeyHex)
        {
            _logger.LogDebug("HandleBotMessage: skipping self-echo");
            return;
        }

        // Deduplicate by event ID
        if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
            await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("HandleBotMessage: skipping already-processed event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Find or create bot chat for this sender
        var chat = await GetOrCreateBotChatAsync(senderPubKey);

        // Content is already decrypted during gift-wrap unwrapping
        var content = nostrEvent.Content;

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chat.Id,
            SenderPublicKey = senderPubKey,
            Type = MessageType.Text,
            Content = content,
            NostrEventId = nostrEvent.EventId,
            Timestamp = nostrEvent.CreatedAt,
            ReceivedAt = DateTime.UtcNow,
            Status = MessageStatus.Delivered,
            IsFromCurrentUser = false
        };

        message.Sender = await _storageService.GetUserByPublicKeyAsync(senderPubKey);
        if (message.Sender == null)
        {
            _ = FetchUnknownSenderAsync(message);
        }

        await _storageService.SaveMessageAsync(message);
        _newMessages.OnNext(message);

        chat.LastActivityAt = DateTime.UtcNow;
        chat.LastMessage = message;
        chat.UnreadCount++;
        await _storageService.SaveChatAsync(chat);
        _chatUpdates.OnNext(chat);

        _logger.LogInformation("HandleBotMessage: saved message {MsgId} in bot chat {ChatId}",
            message.Id[..Math.Min(8, message.Id.Length)], chat.Id[..Math.Min(8, chat.Id.Length)]);
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

        // Fetch and cache sender profile
        string? senderDisplayName = null;
        try
        {
            var metadata = await FetchAndCacheProfileAsync(nostrEvent.PublicKey);
            senderDisplayName = metadata?.DisplayName ?? metadata?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/cache sender metadata for welcome");
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
            _logger.LogWarning("HandleGroupMessage: no 'h' or 'g' tag in kind 445 event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        var groupIdHex = groupTag[1];
        var groupId = Convert.FromHexString(groupIdHex);
        _logger.LogInformation("HandleGroupMessage: kind 445 for group {GroupId} from {Sender}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)],
            nostrEvent.PublicKey[..Math.Min(16, nostrEvent.PublicKey.Length)]);

        // Find chat with this group ID — check both NostrGroupId (h-tag routing) and MlsGroupId (legacy)
        var chats = await _storageService.GetAllChatsAsync();
        var chat = chats.FirstOrDefault(c =>
            (c.NostrGroupId != null && Convert.ToHexString(c.NostrGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase)) ||
            (c.MlsGroupId != null && Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase)));

        if (chat == null)
        {
            _logger.LogWarning("HandleGroupMessage: no chat found for group {GroupId}. Known groups: [{KnownGroups}]",
                groupIdHex[..Math.Min(16, groupIdHex.Length)],
                string.Join(", ", chats.Where(c => c.MlsGroupId != null).Select(c =>
                    (c.NostrGroupId != null ? "nostr:" + Convert.ToHexString(c.NostrGroupId).ToLowerInvariant()[..Math.Min(16, Convert.ToHexString(c.NostrGroupId).Length)] + "/" : "") +
                    "mls:" + Convert.ToHexString(c.MlsGroupId!).ToLowerInvariant()[..Math.Min(16, Convert.ToHexString(c.MlsGroupId!).Length)])));
            return;
        }

        // Deduplicate: skip events already processed and saved to DB.
        // This also catches self-echoes (our own messages saved locally by SendMessageAsync
        // before the relay echoes them back). With MIP-03 ephemeral keys, we can't use
        // pubkey comparison for self-echo detection since the event pubkey is ephemeral.
        if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
            await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
        {
            _logger.LogDebug("HandleGroupMessage: skipping already-processed event {EventId}",
                nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
            return;
        }

        // Decrypt message — must use the MLS group ID (not the NostrGroupId from the h-tag)
        var encryptedData = Convert.FromBase64String(nostrEvent.Content);
        _logger.LogInformation("HandleGroupMessage: decrypting {Len} bytes for chat {ChatName}", encryptedData.Length, chat.Name);

        MlsDecryptedMessage decrypted;
        try
        {
            decrypted = await _mlsService.DecryptMessageAsync(chat.MlsGroupId!, encryptedData);
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

        // Commit messages advance the MLS epoch but carry no user-visible content.
        // The DecryptMessageAsync call already updated the group state.
        if (decrypted.IsCommit)
        {
            _logger.LogInformation("HandleGroupMessage: processed commit for group {GroupId}, epoch advanced",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            return;
        }

        _logger.LogInformation("HandleGroupMessage: decrypted message from {Sender}, epoch={Epoch}, content length={Len}, hasImage={HasImage}, rumorKind={Kind}",
            decrypted.SenderPublicKey[..Math.Min(16, decrypted.SenderPublicKey.Length)], decrypted.Epoch, decrypted.Plaintext.Length,
            decrypted.ImageUrl != null, decrypted.RumorKind);

        // Handle reaction events (kind 7) — update the target message instead of creating a new one
        if (decrypted.RumorKind == 7 && !string.IsNullOrEmpty(decrypted.ReactionTargetEventId) && !string.IsNullOrEmpty(decrypted.ReactionEmoji))
        {
            _logger.LogInformation("HandleGroupMessage: processing reaction {Emoji} on event {TargetEvent} from {Sender}",
                decrypted.ReactionEmoji, decrypted.ReactionTargetEventId[..Math.Min(16, decrypted.ReactionTargetEventId.Length)],
                decrypted.SenderPublicKey[..Math.Min(16, decrypted.SenderPublicKey.Length)]);

            var targetMessage = await _storageService.GetMessageByNostrEventIdAsync(decrypted.ReactionTargetEventId);
            if (targetMessage != null)
            {
                if (!targetMessage.Reactions.ContainsKey(decrypted.ReactionEmoji))
                    targetMessage.Reactions[decrypted.ReactionEmoji] = new List<string>();

                if (!targetMessage.Reactions[decrypted.ReactionEmoji].Contains(decrypted.SenderPublicKey))
                {
                    targetMessage.Reactions[decrypted.ReactionEmoji].Add(decrypted.SenderPublicKey);
                    await _storageService.SaveMessageAsync(targetMessage);
                    _reactionUpdates.OnNext((targetMessage.Id, decrypted.ReactionEmoji, decrypted.SenderPublicKey));
                    _logger.LogInformation("HandleGroupMessage: added reaction {Emoji} to message {MessageId}", decrypted.ReactionEmoji, targetMessage.Id);
                }
            }
            else
            {
                _logger.LogWarning("HandleGroupMessage: reaction target event {EventId} not found in local DB", decrypted.ReactionTargetEventId);
            }

            // Mark this event as processed so relay echoes are skipped
            if (!string.IsNullOrEmpty(nostrEvent.EventId))
            {
                var reactionMarker = new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ChatId = chat.Id,
                    SenderPublicKey = decrypted.SenderPublicKey,
                    Type = MessageType.Text,
                    Content = string.Empty,
                    NostrEventId = nostrEvent.EventId,
                    Timestamp = nostrEvent.CreatedAt,
                    IsDeleted = true // Hidden from UI
                };
                await _storageService.SaveMessageAsync(reactionMarker);
            }
            return;
        }

        // Determine message type and content based on imeta/image metadata
        var messageType = MessageType.Text;
        var content = decrypted.Plaintext;
        if (!string.IsNullOrEmpty(decrypted.ImageUrl))
        {
            if (decrypted.MediaType?.StartsWith("audio/") == true)
            {
                messageType = MessageType.Audio;
                if (string.IsNullOrEmpty(content))
                    content = "[Voice message]";
            }
            else if (decrypted.MediaType?.StartsWith("image/") == true || string.IsNullOrEmpty(decrypted.MediaType))
            {
                messageType = MessageType.Image;
                if (string.IsNullOrEmpty(content))
                    content = "[Image]";
            }
            else
            {
                messageType = MessageType.File;
                if (string.IsNullOrEmpty(content))
                    content = $"[File: {decrypted.FileName ?? "attachment"}]";
            }
        }

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ChatId = chat.Id,
            SenderPublicKey = decrypted.SenderPublicKey,
            Type = messageType,
            Content = content,
            ImageUrl = decrypted.ImageUrl,
            FileName = decrypted.FileName,
            FileSha256 = decrypted.FileSha256,
            EncryptionNonce = decrypted.EncryptionNonce,
            MediaType = decrypted.MediaType,
            EncryptionVersion = decrypted.EncryptionVersion,
            NostrEventId = nostrEvent.EventId,
            RumorEventId = decrypted.RumorEventId,
            MlsEpoch = decrypted.Epoch,
            Timestamp = nostrEvent.CreatedAt,
            ReceivedAt = DateTime.UtcNow,
            Status = MessageStatus.Delivered,
            IsFromCurrentUser = decrypted.SenderPublicKey == _currentUser?.PublicKeyHex
        };

        // Resolve reply-to reference if present
        if (!string.IsNullOrEmpty(decrypted.ReplyToRumorEventId))
        {
            var replyTarget = await _storageService.GetMessageByRumorEventIdAsync(decrypted.ReplyToRumorEventId)
                           ?? await _storageService.GetMessageByNostrEventIdAsync(decrypted.ReplyToRumorEventId);
            if (replyTarget != null)
            {
                message.ReplyToMessageId = replyTarget.Id;
                message.ReplyToMessage = replyTarget;
                _logger.LogInformation("HandleGroupMessage: resolved reply to message {MessageId}", replyTarget.Id);
            }
            else
            {
                _logger.LogDebug("HandleGroupMessage: reply target {EventId} not found in local DB",
                    decrypted.ReplyToRumorEventId[..Math.Min(16, decrypted.ReplyToRumorEventId.Length)]);
            }
        }

        message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);

        // If sender is unknown, fetch their profile in the background so the message
        // is delivered immediately while the profile resolves asynchronously.
        if (message.Sender == null && !message.IsFromCurrentUser)
        {
            _ = FetchUnknownSenderAsync(message);
        }

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

        MlsGroupInfo groupInfo;
        try
        {
            groupInfo = await _mlsService.ProcessWelcomeAsync(invite.WelcomeData, invite.NostrEventId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("KeyPackage"))
        {
            // The private key material for the targeted KeyPackage is lost — this invite
            // can never be accepted on this device. Auto-dismiss so it doesn't stay stuck.
            _logger.LogWarning(ex, "AcceptInvite: KeyPackage mismatch for invite {InviteId}, auto-dismissing", inviteId);
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
            await _storageService.DeletePendingInviteAsync(inviteId);
            throw new InvalidOperationException(
                "This invite targets a KeyPackage whose private key is no longer available. " +
                "The invite has been dismissed. Ask the sender to create a new invite.", ex);
        }

        // Check if we already have a chat for this MLS group (prevents duplicate chats)
        var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
        var existingChats = await _storageService.GetAllChatsAsync();
        var existingChat = existingChats.FirstOrDefault(c =>
            c.MlsGroupId != null &&
            Convert.ToHexString(c.MlsGroupId).Equals(groupIdHex, StringComparison.OrdinalIgnoreCase));

        if (existingChat != null)
        {
            _logger.LogInformation("Chat already exists for group {GroupId}, returning existing chat {ChatId}",
                groupIdHex[..Math.Min(16, groupIdHex.Length)], existingChat.Id);
            await _storageService.DismissWelcomeEventAsync(invite.NostrEventId);
            await _storageService.DeletePendingInviteAsync(inviteId);
            return existingChat;
        }

        // Create chat for the group — fall back to sender name if the MLS Welcome
        // didn't carry a group name (e.g., Rust-originated or older welcomes)
        var chatName = !string.IsNullOrWhiteSpace(groupInfo.GroupName)
            ? groupInfo.GroupName
            : !string.IsNullOrWhiteSpace(invite.SenderDisplayName)
                ? $"Chat with {invite.SenderDisplayName}"
                : $"Group {invite.SenderPublicKey[..8]}";

        // Get the NostrGroupId (from 0xF2EE extension) for relay subscriptions and h-tag routing
        var nostrGroupId = _mlsService.GetNostrGroupId(groupInfo.GroupId);

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = chatName,
            Type = ChatType.Group,
            MlsGroupId = groupInfo.GroupId,
            NostrGroupId = nostrGroupId,
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

        // Mark the consumed KeyPackage as used so future senders don't reuse it
        if (!string.IsNullOrEmpty(invite.KeyPackageEventId))
        {
            var consumedKp = await _storageService.GetKeyPackageByNostrEventIdAsync(invite.KeyPackageEventId);
            if (consumedKp != null)
            {
                await _storageService.MarkKeyPackageUsedAsync(consumedKp.Id);
                _logger.LogInformation("AcceptInvite: marked KeyPackage {KpId} as used (event {EventId})",
                    consumedKp.Id, invite.KeyPackageEventId[..Math.Min(16, invite.KeyPackageEventId.Length)]);

                // Auto-publish a new KeyPackage if this was the last unused one
                _ = AutoPublishKeyPackageIfNeededAsync();
            }
            else
            {
                _logger.LogDebug("AcceptInvite: KeyPackage with event ID {EventId} not found in local DB, skipping mark-as-used",
                    invite.KeyPackageEventId[..Math.Min(16, invite.KeyPackageEventId.Length)]);
            }
        }

        _logger.LogInformation("Accepted invite {InviteId}, created chat {ChatId} for group {GroupName}", inviteId, chat.Id, chat.Name);
        return chat;
    }

    private async Task AutoPublishKeyPackageIfNeededAsync()
    {
        try
        {
            if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
                return;

            var remaining = await _storageService.GetUnusedKeyPackagesAsync(_currentUser.PublicKeyHex);
            if (remaining.Any())
            {
                _logger.LogDebug("AutoPublishKP: {Count} unused KeyPackages remaining, no action needed", remaining.Count());
                return;
            }

            _logger.LogInformation("AutoPublishKP: no unused KeyPackages remaining, generating and publishing a new one");

            var keyPackage = await _mlsService.GenerateKeyPackageAsync();
            var eventId = await _nostrService.PublishKeyPackageAsync(
                keyPackage.Data, _currentUser.PrivateKeyHex, keyPackage.NostrTags);
            keyPackage.NostrEventId = eventId;
            await _storageService.SaveKeyPackageAsync(keyPackage);

            _logger.LogInformation("AutoPublishKP: published new KeyPackage {EventId}", eventId[..Math.Min(16, eventId.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoPublishKP: failed to auto-publish new KeyPackage");
        }
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
        var welcomeEvents = await _nostrService.FetchWelcomeEventsAsync(_currentUser.PublicKeyHex, _currentUser.PrivateKeyHex);

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

    public async Task<KeyPackageAuditResult> AuditKeyPackagesAsync()
    {
        if (_currentUser == null || string.IsNullOrEmpty(_currentUser.PublicKeyHex))
            throw new InvalidOperationException("No current user. Initialize first.");

        _logger.LogInformation("AuditKeyPackages: fetching own KeyPackages from relays for {PubKey}",
            _currentUser.PublicKeyHex[..Math.Min(16, _currentUser.PublicKeyHex.Length)]);

        var result = new KeyPackageAuditResult();

        // Fetch all our KeyPackages from relays
        var relayKeyPackages = (await _nostrService.FetchKeyPackagesAsync(_currentUser.PublicKeyHex)).ToList();
        result.TotalOnRelays = relayKeyPackages.Count;

        _logger.LogInformation("AuditKeyPackages: found {Count} KeyPackages on relays", relayKeyPackages.Count);

        var storedKpCount = _mlsService.GetStoredKeyPackageCount();
        _logger.LogInformation("AuditKeyPackages: {Count} KeyPackages with local private keys",
            storedKpCount >= 0 ? storedKpCount.ToString() : "unknown (native backend)");

        foreach (var relayKp in relayKeyPackages)
        {
            // Check if expired
            if (relayKp.ExpiresAt < DateTime.UtcNow)
            {
                result.Expired++;
                relayKp.Status = KeyPackageStatus.Expired;
                _logger.LogDebug("AuditKeyPackages: KeyPackage {EventId} is expired",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }
            // Check if we have the private key material locally
            else if (_mlsService.HasKeyMaterialForKeyPackage(relayKp.Data))
            {
                result.ActiveWithKeys++;
                relayKp.Status = KeyPackageStatus.Active;
                _logger.LogDebug("AuditKeyPackages: KeyPackage {EventId} has local keys (active)",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }
            else
            {
                result.Lost++;
                relayKp.Status = KeyPackageStatus.Lost;
                if (!string.IsNullOrEmpty(relayKp.NostrEventId))
                    result.LostEventIds.Add(relayKp.NostrEventId);
                _logger.LogWarning("AuditKeyPackages: KeyPackage {EventId} has NO local keys (LOST)",
                    relayKp.NostrEventId?[..Math.Min(16, relayKp.NostrEventId?.Length ?? 0)] ?? "unknown");
            }

            // Save/update in local DB
            await _storageService.SaveKeyPackageAsync(relayKp);
        }

        _logger.LogInformation(
            "AuditKeyPackages: complete — {Total} on relays, {Active} active, {Lost} lost, {Expired} expired",
            result.TotalOnRelays, result.ActiveWithKeys, result.Lost, result.Expired);

        return result;
    }

    public async Task<LoadOlderMessagesResult> LoadOlderMessagesAsync(string chatId, DateTimeOffset fetchBoundary)
    {
        _logger.LogInformation("LoadOlderMessages: starting for chat {ChatId}, boundary={Boundary}",
            chatId, fetchBoundary);

        var result = new LoadOlderMessagesResult();

        var chat = await _storageService.GetChatAsync(chatId);
        if (chat == null)
        {
            _logger.LogWarning("LoadOlderMessages: chat {ChatId} not found", chatId);
            result.HasMore = false;
            result.NewBoundary = fetchBoundary;
            return result;
        }

        if (chat.MlsGroupId == null)
        {
            _logger.LogWarning("LoadOlderMessages: chat {ChatId} has no MLS group, cannot load history", chatId);
            result.HasMore = false;
            result.NewBoundary = fetchBoundary;
            return result;
        }

        // Use NostrGroupId for the relay query h-tag filter, fall back to MlsGroupId
        var groupIdForQuery = chat.NostrGroupId ?? chat.MlsGroupId;
        var groupIdHex = Convert.ToHexString(groupIdForQuery).ToLowerInvariant();

        _logger.LogDebug("LoadOlderMessages: querying group {GroupId} (NostrGroupId={HasNostr})",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], chat.NostrGroupId != null);

        var chatCreatedAt = new DateTimeOffset(chat.CreatedAt, TimeSpan.Zero);
        var until = fetchBoundary;
        var retries = 0;
        const int maxRetries = 3;
        var fetchedEvents = new List<NostrEventReceived>();

        // 2-day sliding window with retry
        while (retries < maxRetries)
        {
            var since = until.AddDays(-2);

            // Clamp since to chat creation time
            if (since < chatCreatedAt)
            {
                since = chatCreatedAt;
            }

            _logger.LogDebug("LoadOlderMessages: window attempt {Attempt}, since={Since}, until={Until}",
                retries + 1, since, until);

            var events = await _nostrService.FetchGroupHistoryAsync(groupIdHex, since, until, 50);
            var eventList = events.ToList();

            if (eventList.Count > 0)
            {
                fetchedEvents.AddRange(eventList);
                result.NewBoundary = since;
                _logger.LogInformation("LoadOlderMessages: found {Count} events in window", eventList.Count);
                break;
            }

            // No results — move window back and retry
            retries++;
            until = since;

            if (since <= chatCreatedAt)
            {
                _logger.LogInformation("LoadOlderMessages: reached chat creation time, no more history");
                result.HasMore = false;
                result.NewBoundary = chatCreatedAt;
                return result;
            }

            _logger.LogDebug("LoadOlderMessages: no results, retrying (attempt {Attempt}/{Max})",
                retries + 1, maxRetries);
        }

        if (fetchedEvents.Count == 0)
        {
            _logger.LogInformation("LoadOlderMessages: no events found after {Retries} retries", maxRetries);
            result.HasMore = until > chatCreatedAt;
            result.NewBoundary = until;
            return result;
        }

        // Process fetched events: dedup, skip own, decrypt, build messages
        var processedCount = 0;
        var skipCount = 0;
        var decryptFailCount = 0;
        var unknownSenderKeys = new HashSet<string>();

        foreach (var nostrEvent in fetchedEvents)
        {
            // Dedup by NostrEventId — skip events already in the database
            if (!string.IsNullOrEmpty(nostrEvent.EventId) &&
                await _storageService.MessageExistsByNostrEventIdAsync(nostrEvent.EventId))
            {
                skipCount++;
                continue;
            }

            // Skip own messages (already saved locally by SendMessageAsync)
            if (nostrEvent.PublicKey == _currentUser?.PublicKeyHex)
            {
                skipCount++;
                continue;
            }

            // Decrypt
            var encryptedData = Convert.FromBase64String(nostrEvent.Content);

            MlsDecryptedMessage decrypted;
            try
            {
                // Use MlsGroupId for decrypt, NOT the NostrGroupId from the h-tag
                decrypted = await _mlsService.DecryptMessageAsync(chat.MlsGroupId!, encryptedData);
            }
            catch (Exception ex)
            {
                decryptFailCount++;
                _logger.LogWarning(ex, "LoadOlderMessages: decrypt failed for event {EventId}, skipping",
                    nostrEvent.EventId[..Math.Min(16, nostrEvent.EventId.Length)]);
                continue;
            }

            // Determine message type based on imeta/image metadata
            var olderMsgType = MessageType.Text;
            var olderContent = decrypted.Plaintext;
            if (!string.IsNullOrEmpty(decrypted.ImageUrl))
            {
                if (decrypted.MediaType?.StartsWith("audio/") == true)
                {
                    olderMsgType = MessageType.Audio;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = "[Voice message]";
                }
                else if (decrypted.MediaType?.StartsWith("image/") == true || string.IsNullOrEmpty(decrypted.MediaType))
                {
                    olderMsgType = MessageType.Image;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = "[Image]";
                }
                else
                {
                    olderMsgType = MessageType.File;
                    if (string.IsNullOrEmpty(olderContent))
                        olderContent = $"[File: {decrypted.FileName ?? "attachment"}]";
                }
            }

            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ChatId = chat.Id,
                SenderPublicKey = decrypted.SenderPublicKey,
                Type = olderMsgType,
                Content = olderContent,
                ImageUrl = decrypted.ImageUrl,
                FileName = decrypted.FileName,
                FileSha256 = decrypted.FileSha256,
                EncryptionNonce = decrypted.EncryptionNonce,
                MediaType = decrypted.MediaType,
                EncryptionVersion = decrypted.EncryptionVersion,
                NostrEventId = nostrEvent.EventId,
                MlsEpoch = decrypted.Epoch,
                Timestamp = nostrEvent.CreatedAt,
                ReceivedAt = DateTime.UtcNow,
                Status = MessageStatus.Delivered,
                IsFromCurrentUser = decrypted.SenderPublicKey == _currentUser?.PublicKeyHex
            };

            message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);

            // Queue unknown senders for batch profile fetch below
            if (message.Sender == null && !message.IsFromCurrentUser)
            {
                unknownSenderKeys.Add(message.SenderPublicKey);
            }

            await _storageService.SaveMessageAsync(message);
            result.Messages.Add(message);
            processedCount++;
        }

        // Batch-fetch profiles for unknown senders
        if (unknownSenderKeys.Count > 0)
        {
            _logger.LogInformation("LoadOlderMessages: fetching profiles for {Count} unknown senders", unknownSenderKeys.Count);
            foreach (var pubKey in unknownSenderKeys)
            {
                try
                {
                    var metadata = await FetchAndCacheProfileAsync(pubKey);
                    if (metadata != null)
                    {
                        // Backfill sender on already-processed messages
                        var user = await _storageService.GetUserByPublicKeyAsync(pubKey);
                        foreach (var msg in result.Messages.Where(m => m.SenderPublicKey == pubKey && m.Sender == null))
                        {
                            msg.Sender = user;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch profile for unknown sender {PubKey}", pubKey[..Math.Min(16, pubKey.Length)]);
                }
            }
        }

        _logger.LogInformation(
            "LoadOlderMessages: processed {Processed} messages, skipped {Skipped}, decrypt failures {DecryptFail}",
            processedCount, skipCount, decryptFailCount);

        // Sort by timestamp ascending (oldest first)
        result.Messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Check if we've reached the beginning
        if (result.NewBoundary <= chatCreatedAt)
        {
            result.HasMore = false;
        }

        return result;
    }

    public async Task<UserMetadata?> FetchAndCacheProfileAsync(string publicKeyHex)
    {
        try
        {
            var metadata = await _nostrService.FetchUserMetadataAsync(publicKeyHex);
            if (metadata != null && !string.IsNullOrEmpty(metadata.PublicKeyHex))
            {
                await SaveMetadataAsUserAsync(metadata);
            }
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchAndCacheProfile failed for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
            return null;
        }
    }

    /// <summary>
    /// Background task: fetches profile for an unknown message sender and emits a message update
    /// so the UI can refresh the display name without blocking message delivery.
    /// </summary>
    private async Task FetchUnknownSenderAsync(Message message)
    {
        try
        {
            _logger.LogInformation("Fetching profile for unknown sender {PubKey}", message.SenderPublicKey[..Math.Min(16, message.SenderPublicKey.Length)]);
            var metadata = await FetchAndCacheProfileAsync(message.SenderPublicKey);
            if (metadata != null)
            {
                message.Sender = await _storageService.GetUserByPublicKeyAsync(message.SenderPublicKey);
                // Re-emit so the UI updates the display name
                _newMessages.OnNext(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background profile fetch failed for sender {PubKey}", message.SenderPublicKey[..Math.Min(16, message.SenderPublicKey.Length)]);
        }
    }

    /// <summary>
    /// Converts UserMetadata to a User and saves to the database (upsert).
    /// Preserves existing fields (private keys, signer details, IsCurrentUser) if the user already exists.
    /// </summary>
    private async Task SaveMetadataAsUserAsync(UserMetadata metadata)
    {
        try
        {
            var existing = await _storageService.GetUserByPublicKeyAsync(metadata.PublicKeyHex);
            if (existing != null)
            {
                // Update profile fields only, preserve everything else
                existing.DisplayName = metadata.DisplayName ?? metadata.Name ?? existing.DisplayName;
                existing.Username = metadata.Username ?? existing.Username;
                existing.AvatarUrl = metadata.Picture ?? existing.AvatarUrl;
                existing.About = metadata.About ?? existing.About;
                existing.Nip05 = metadata.Nip05 ?? existing.Nip05;
                existing.LastUpdatedAt = DateTime.UtcNow;

                // Download avatar if URL changed or no local copy
                if (!string.IsNullOrEmpty(existing.AvatarUrl) &&
                    (existing.LocalAvatarPath == null || !File.Exists(existing.LocalAvatarPath)))
                {
                    existing.LocalAvatarPath = await DownloadAvatarAsync(metadata.PublicKeyHex, existing.AvatarUrl);
                }

                await _storageService.SaveUserAsync(existing);
                _logger.LogDebug("Updated cached profile for {PubKey}", metadata.PublicKeyHex[..16]);
            }
            else
            {
                var npub = metadata.Npub ?? Bech32.Encode("npub", Convert.FromHexString(metadata.PublicKeyHex));
                var user = new User
                {
                    Id = Guid.NewGuid().ToString(),
                    PublicKeyHex = metadata.PublicKeyHex,
                    Npub = npub,
                    DisplayName = metadata.DisplayName ?? metadata.Name ?? string.Empty,
                    Username = metadata.Username,
                    AvatarUrl = metadata.Picture,
                    About = metadata.About,
                    Nip05 = metadata.Nip05,
                    CreatedAt = metadata.CreatedAt ?? DateTime.UtcNow,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsCurrentUser = false
                };

                // Download avatar
                if (!string.IsNullOrEmpty(user.AvatarUrl))
                {
                    user.LocalAvatarPath = await DownloadAvatarAsync(metadata.PublicKeyHex, user.AvatarUrl);
                }

                await _storageService.SaveUserAsync(user);
                _logger.LogInformation("Cached new profile for {PubKey}: {Name}", metadata.PublicKeyHex[..16], user.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache metadata for {PubKey}", metadata.PublicKeyHex[..16]);
        }
    }

    private async Task<string?> DownloadAvatarAsync(string publicKeyHex, string avatarUrl)
    {
        try
        {
            var avatarDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenChat", "avatars");
            Directory.CreateDirectory(avatarDir);

            var extension = Path.GetExtension(new Uri(avatarUrl).AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length > 5)
                extension = ".jpg";
            var localPath = Path.Combine(avatarDir, $"{publicKeyHex[..16]}{extension}");

            // Skip if already downloaded
            if (File.Exists(localPath))
                return localPath;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await httpClient.GetAsync(avatarUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Avatar download failed for {PubKey}: HTTP {Status}", publicKeyHex[..16], response.StatusCode);
                return null;
            }

            // Limit to 2MB
            var content = await response.Content.ReadAsByteArrayAsync();
            if (content.Length > 2 * 1024 * 1024)
            {
                _logger.LogWarning("Avatar too large for {PubKey}: {Size} bytes", publicKeyHex[..16], content.Length);
                return null;
            }

            await File.WriteAllBytesAsync(localPath, content);
            _logger.LogInformation("Downloaded avatar for {PubKey}: {Path} ({Size} bytes)", publicKeyHex[..16], localPath, content.Length);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download avatar for {PubKey}", publicKeyHex[..16]);
            return null;
        }
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
        _reactionUpdates.Dispose();

        _disposed = true;
    }
}
