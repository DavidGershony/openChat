using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
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
    private IDisposable? _chatUpdateSubscription;
    private IDisposable? _inviteSubscription;
    private IDisposable? _decryptionErrorSubscription;

    public ObservableCollection<ChatItemViewModel> Chats { get; } = new();
    public ObservableCollection<PendingInviteItemViewModel> PendingInvites { get; } = new();

    [Reactive] public ChatItemViewModel? SelectedChat { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public int PendingInviteCount { get; set; }
    [Reactive] public string? StatusMessage { get; set; }

    // New Chat Dialog
    [Reactive] public bool ShowNewChatDialog { get; set; }
    [Reactive] public string NewChatPublicKey { get; set; } = string.Empty;
    [Reactive] public string NewChatName { get; set; } = string.Empty;
    [Reactive] public string? NewChatError { get; set; }
    [Reactive] public bool IsLookingUpKeyPackage { get; set; }
    [Reactive] public string? KeyPackageStatus { get; set; }
    [Reactive] public bool HasKeyPackage { get; set; }
    [Reactive] public DateTime? KeyPackageCreatedAt { get; set; }
    [Reactive] public string? KeyPackageRelays { get; set; }

    // New Group Dialog
    [Reactive] public bool ShowNewGroupDialog { get; set; }
    [Reactive] public string NewGroupName { get; set; } = string.Empty;
    [Reactive] public string NewGroupDescription { get; set; } = string.Empty;
    [Reactive] public string NewGroupMembers { get; set; } = string.Empty;
    [Reactive] public string? NewGroupError { get; set; }
    [Reactive] public bool IsLookingUpGroupKeyPackages { get; set; }
    [Reactive] public string? GroupKeyPackageStatus { get; set; }

    // Join Group Dialog
    [Reactive] public bool ShowJoinGroupDialog { get; set; }
    [Reactive] public string JoinGroupId { get; set; } = string.Empty;
    [Reactive] public string? JoinGroupError { get; set; }

    // Delete Chat Dialog
    [Reactive] public bool ShowDeleteChatDialog { get; set; }
    [Reactive] public ChatItemViewModel? ChatToDelete { get; set; }

    // Reset Group Dialog
    [Reactive] public bool ShowResetGroupDialog { get; set; }
    [Reactive] public ChatItemViewModel? GroupToReset { get; set; }

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
    public ReactiveCommand<Unit, Unit> ConfirmResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelResetGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupKeyPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> LookupGroupKeyPackagesCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<PendingInviteItemViewModel, Unit> DeclineInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanInvitesCommand { get; }
    [Reactive] public bool IsRescanningInvites { get; set; }

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
            KeyPackageStatus = null;
            HasKeyPackage = false;
            KeyPackageCreatedAt = null;
            KeyPackageRelays = null;
            ShowNewChatDialog = true;
        });

        NewGroupCommand = ReactiveCommand.Create(() =>
        {
            _logger.LogInformation("Opening new group dialog");
            NewGroupName = string.Empty;
            NewGroupDescription = string.Empty;
            NewGroupMembers = string.Empty;
            NewGroupError = null;
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
            name => !string.IsNullOrWhiteSpace(name) && name.Length >= 1);

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

        var canLookupKeyPackage = this.WhenAnyValue(
            x => x.NewChatPublicKey,
            x => x.IsLookingUpKeyPackage,
            (key, looking) => !string.IsNullOrWhiteSpace(key) && key.Length >= 8 && !looking);

        LookupKeyPackageCommand = ReactiveCommand.CreateFromTask(LookupKeyPackageAsync, canLookupKeyPackage);

        var canLookupGroupKeyPackages = this.WhenAnyValue(
            x => x.NewGroupMembers,
            x => x.IsLookingUpGroupKeyPackages,
            (members, looking) => !string.IsNullOrWhiteSpace(members) && !looking);

        LookupGroupKeyPackagesCommand = ReactiveCommand.CreateFromTask(LookupGroupKeyPackagesAsync, canLookupGroupKeyPackages);

        AcceptInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(AcceptInviteAsync);
        DeclineInviteCommand = ReactiveCommand.CreateFromTask<PendingInviteItemViewModel>(DeclineInviteAsync);

        var canRescan = this.WhenAnyValue(x => x.IsRescanningInvites, scanning => !scanning);
        RescanInvitesCommand = ReactiveCommand.CreateFromTask(RescanInvitesAsync, canRescan);

        var canCreateChat = this.WhenAnyValue(
            x => x.NewChatPublicKey,
            pubKey => !string.IsNullOrWhiteSpace(pubKey) && pubKey.Length >= 8);

        CreateChatCommand = ReactiveCommand.CreateFromTask(CreateNewChatAsync, canCreateChat);

        CancelNewChatCommand = ReactiveCommand.Create(() =>
        {
            ShowNewChatDialog = false;
            NewChatError = null;
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
            var chats = await _messageService.GetChatsAsync();

            Chats.Clear();
            foreach (var chat in chats.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastActivityAt))
            {
                Chats.Add(new ChatItemViewModel(chat));
            }

            // Load pending invites
            var invites = await _messageService.GetPendingInvitesAsync();
            PendingInvites.Clear();
            foreach (var invite in invites)
            {
                PendingInvites.Add(new PendingInviteItemViewModel(invite));
            }
            PendingInviteCount = PendingInvites.Count;
        }
        finally
        {
            IsLoading = false;
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
        KeyPackageStatus = "Looking up KeyPackage...";
        HasKeyPackage = false;
        KeyPackageCreatedAt = null;
        KeyPackageRelays = null;

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
            var keyPackageList = keyPackages.ToList();

            if (keyPackageList.Count > 0)
            {
                var latestPackage = keyPackageList.OrderByDescending(k => k.CreatedAt).First();
                HasKeyPackage = true;
                KeyPackageCreatedAt = latestPackage.CreatedAt;
                KeyPackageRelays = latestPackage.RelayUrls.Count > 0
                    ? string.Join(", ", latestPackage.RelayUrls.Take(3))
                    : "Unknown relay";
                KeyPackageStatus = $"Found {keyPackageList.Count} KeyPackage(s)";
                _logger.LogInformation("Found {Count} KeyPackage(s) for {PubKey}", keyPackageList.Count, pubKey[..Math.Min(16, pubKey.Length)]);
            }
            else
            {
                HasKeyPackage = false;
                KeyPackageStatus = "No KeyPackage found - user cannot join MLS groups";
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

    private async Task CreateNewChatAsync()
    {
        if (string.IsNullOrWhiteSpace(NewChatPublicKey))
        {
            NewChatError = "Please enter a public key (npub or hex)";
            return;
        }

        if (_mlsService == null || _nostrService == null)
        {
            NewChatError = "MLS or Nostr service not available";
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
                catch
                {
                    NewChatError = "Invalid npub format";
                    return;
                }
            }

            // Resolve chat name: user-provided > profile metadata > truncated key
            var chatName = NewChatName;
            if (string.IsNullOrWhiteSpace(chatName))
            {
                try
                {
                    var metadata = await _nostrService.FetchUserMetadataAsync(publicKeyHex);
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
            if (currentUser == null || string.IsNullOrEmpty(currentUser.PrivateKeyHex))
            {
                NewChatError = "Not logged in";
                return;
            }

            // Create MLS group
            _logger.LogDebug("Creating MLS group for chat...");
            var groupInfo = await _mlsService.CreateGroupAsync(chatName);
            var groupIdHex = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant();
            await PersistMlsGroupStateAsync(groupInfo.GroupId);

            var chat = new Chat
            {
                Id = groupIdHex,
                Name = chatName,
                Type = ChatType.Group,
                ParticipantPublicKeys = new List<string>(groupInfo.MemberPublicKeys),
                MlsGroupId = groupInfo.GroupId,
                MlsEpoch = groupInfo.Epoch,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            _logger.LogInformation("Created MLS group with ID: {GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

            // Fetch recipient's KeyPackage
            _logger.LogDebug("Fetching KeyPackage for {PubKey}", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);
            var keyPackages = await _nostrService.FetchKeyPackagesAsync(publicKeyHex);
            var keyPackage = keyPackages.FirstOrDefault();

            if (keyPackage == null)
            {
                NewChatError = "No KeyPackage found for this user — they cannot receive invites";
                return;
            }

            // Add member to MLS group
            _logger.LogDebug("Adding member to MLS group");
            var welcome = await _mlsService.AddMemberAsync(chat.MlsGroupId, keyPackage);
            await PersistMlsGroupStateAsync(chat.MlsGroupId);

            // Publish Commit (kind 445) before Welcome (kind 444) per MIP-02
            if (welcome.CommitData != null && welcome.CommitData.Length > 0)
            {
                _logger.LogDebug("Publishing Commit message to Nostr (kind 445)");
                var commitEventId = await _nostrService.PublishCommitAsync(
                    welcome.CommitData, groupIdHex, currentUser.PrivateKeyHex);
                _logger.LogInformation("Published Commit {EventId} for group {GroupId}",
                    commitEventId[..Math.Min(16, commitEventId.Length)], groupIdHex[..Math.Min(16, groupIdHex.Length)]);
                await Task.Delay(500);
            }

            // Publish Welcome (kind 444)
            _logger.LogDebug("Publishing Welcome message to Nostr (kind 444)");
            var welcomeEventId = await _nostrService.PublishWelcomeAsync(
                welcome.WelcomeData, publicKeyHex, currentUser.PrivateKeyHex,
                welcome.KeyPackageEventId);
            _logger.LogInformation("Published Welcome {EventId} for {PubKey}",
                welcomeEventId[..Math.Min(16, welcomeEventId.Length)],
                publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

            // Add recipient to participant list
            if (!chat.ParticipantPublicKeys.Contains(publicKeyHex))
            {
                chat.ParticipantPublicKeys.Add(publicKeyHex);
            }

            await _storageService.SaveChatAsync(chat);
            _logger.LogInformation("Saved chat: {ChatId} - {ChatName}", chat.Id, chatName);

            // Subscribe to group messages for the new group
            await _nostrService.SubscribeToGroupMessagesAsync(new[] { groupIdHex });

            // Add to list and select it
            var chatItem = new ChatItemViewModel(chat);
            Chats.Insert(0, chatItem);
            SelectedChat = chatItem;

            // Close dialog
            ShowNewChatDialog = false;
            NewChatError = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new chat");
            NewChatError = $"Failed to create chat: {ex.Message}";
        }
    }

    private async Task CreateNewGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName))
        {
            NewGroupError = "Please enter a group name";
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
                var groupInfo = await _mlsService.CreateGroupAsync(NewGroupName.Trim());
                await PersistMlsGroupStateAsync(groupInfo.GroupId);

                chat = new Chat
                {
                    Id = Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant(),
                    Name = NewGroupName.Trim(),
                    Description = string.IsNullOrWhiteSpace(NewGroupDescription) ? null : NewGroupDescription.Trim(),
                    Type = ChatType.Group,
                    ParticipantPublicKeys = new List<string>(groupInfo.MemberPublicKeys),
                    MlsGroupId = groupInfo.GroupId,
                    MlsEpoch = groupInfo.Epoch,
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow
                };

                _logger.LogInformation("Created MLS group with ID: {GroupId}", chat.Id);

                // Invite members if we have MLS, Nostr, and a private key
                if (_nostrService != null && !string.IsNullOrEmpty(currentUser?.PrivateKeyHex) && memberKeys.Count > 0)
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
                            await PersistMlsGroupStateAsync(chat.MlsGroupId);

                            // MIP-02: Publish Commit (kind 445) BEFORE sending Welcome (kind 444)
                            // This ensures all existing members receive the commit first
                            if (welcome.CommitData != null && welcome.CommitData.Length > 0)
                            {
                                _logger.LogDebug("Publishing Commit message to Nostr (kind 445)");
                                var commitEventId = await _nostrService.PublishCommitAsync(
                                    welcome.CommitData,
                                    chat.Id,
                                    currentUser.PrivateKeyHex);

                                _logger.LogInformation("Published Commit {EventId} for group {GroupId}",
                                    commitEventId[..Math.Min(16, commitEventId.Length)], chat.Id[..Math.Min(16, chat.Id.Length)]);

                                // Small delay to allow commit to propagate to relays before welcome
                                await Task.Delay(500);
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
                            if (!chat.ParticipantPublicKeys.Contains(publicKeyHex))
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
            if (chat.MlsGroupId != null && chat.MlsGroupId.Length > 0 && _nostrService != null)
            {
                var subGroupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
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

    private async Task PersistMlsGroupStateAsync(byte[] groupId)
    {
        if (_mlsService == null) return;
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
        var existing = Chats.FirstOrDefault(c => c.Id == chat.Id);

        if (existing != null)
        {
            existing.Update(chat);
            // Re-sort if needed
            var index = Chats.IndexOf(existing);
            var newIndex = GetInsertIndex(chat);
            if (index != newIndex && newIndex < Chats.Count)
            {
                Chats.Move(index, newIndex > index ? newIndex - 1 : newIndex);
            }
        }
        else
        {
            var newItem = new ChatItemViewModel(chat);
            var insertIndex = GetInsertIndex(chat);
            Chats.Insert(insertIndex, newItem);
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
        // Avoid duplicates
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

            // Persist MLS state after welcome processing
            if (chat.MlsGroupId != null && _mlsService != null)
                await PersistMlsGroupStateAsync(chat.MlsGroupId);

            // Subscribe to group messages for the newly accepted group
            if (chat.MlsGroupId != null && chat.MlsGroupId.Length > 0 && _nostrService != null)
            {
                var groupIdHex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                await _nostrService.SubscribeToGroupMessagesAsync(new[] { groupIdHex });
            }

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
    [Reactive] public string? LastMessagePreview { get; set; }
    [Reactive] public DateTime LastActivityAt { get; set; }
    [Reactive] public int UnreadCount { get; set; }
    [Reactive] public bool IsPinned { get; set; }
    [Reactive] public bool IsMuted { get; set; }
    [Reactive] public bool IsGroup { get; set; }
    [Reactive] public bool IsVisible { get; set; } = true;

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
        IsPinned = chat.IsPinned;
        IsMuted = chat.IsMuted;
        IsGroup = chat.Type == ChatType.Group;
    }
}

public class PendingInviteItemViewModel : ViewModelBase
{
    public string Id { get; }
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
        SenderPublicKey = invite.SenderPublicKey;
        GroupId = invite.GroupId;
        ReceivedAt = invite.ReceivedAt;

        SenderName = !string.IsNullOrEmpty(invite.SenderDisplayName)
            ? invite.SenderDisplayName
            : invite.SenderPublicKey.Length > 12
                ? $"{invite.SenderPublicKey[..12]}..."
                : invite.SenderPublicKey;

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
