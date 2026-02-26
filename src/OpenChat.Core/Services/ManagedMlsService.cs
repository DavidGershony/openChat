// =============================================================================
// ManagedMlsService — TEMPORARILY DISABLED
// Depends on marmut-mdk NuGet packages (not yet published).
// Uncomment when MarmutMdk.* packages are available on NuGet.
// =============================================================================

#if false

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using MarmutMdk.Core;
using MarmutMdk.Core.Results;
using MarmutMdk.Mls.Codec;
using MarmutMdk.Mls.Crypto;
using MarmutMdk.Mls.Group;
using MarmutMdk.Protocol.Mip00;
using MarmutMdk.Storage.Memory;

namespace OpenChat.Core.Services;

/// <summary>
/// IMlsService implementation backed by the pure C# marmut-mdk library.
/// Uses Mdk&lt;MemoryStorageProvider&gt; for MLS group management.
/// Optionally persists MLS state via IStorageService for cross-restart survival.
/// </summary>
public class ManagedMlsService : IMlsService
{
    private readonly ILogger<ManagedMlsService> _logger;
    private readonly ICipherSuite _cipherSuite = new CipherSuite0x0001();
    private readonly IStorageService? _storageService;

    private Mdk<MemoryStorageProvider>? _mdk;
    private string? _publicKeyHex;
    private byte[]? _identity;
    private byte[]? _signingPrivateKey;
    private byte[]? _signingPublicKey;

    // Stored from last GenerateKeyPackageAsync, needed for ProcessWelcomeAsync
    private byte[]? _storedKeyPackageBytes;
    private byte[]? _storedInitPrivateKey;
    private byte[]? _storedHpkePrivateKey;

    private const string ServiceStateKey = "__service__";
    private const byte ServiceStateVersion = 1;

    public ManagedMlsService(IStorageService? storageService = null)
    {
        _storageService = storageService;
        _logger = LoggingConfiguration.CreateLogger<ManagedMlsService>();
        _logger.LogInformation("ManagedMlsService instance created (C# MDK backend, persistence={HasStorage})",
            storageService != null);
    }

    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        // Guard against double-initialization clobbering restored state
        if (_mdk != null)
        {
            _logger.LogDebug("ManagedMlsService already initialized, skipping");
            return;
        }

        _logger.LogInformation("Initializing managed MLS service");
        _publicKeyHex = publicKeyHex;
        _identity = Convert.FromHexString(publicKeyHex);

        // Try to restore signing keys from persisted service state first
        bool restoredKeys = false;
        if (_storageService != null)
        {
            try
            {
                var serviceState = await _storageService.GetMlsStateAsync(ServiceStateKey);
                if (serviceState != null)
                {
                    await ImportServiceStateAsync(serviceState);
                    restoredKeys = _signingPrivateKey != null && _signingPublicKey != null;
                    _logger.LogInformation("Restored MLS signing keys from persistence");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore MLS service state, will generate new keys");
            }
        }

        // Generate new Ed25519 signing keys only if we couldn't restore them
        if (!restoredKeys)
        {
            (_signingPrivateKey, _signingPublicKey) = _cipherSuite.GenerateSignatureKeyPair();
            _logger.LogInformation("Generated new MLS signing keys");
        }

        var storage = new MemoryStorageProvider();
        _mdk = new MdkBuilder<MemoryStorageProvider>()
            .WithStorage(storage)
            .WithConfig(MdkConfig.Default)
            .Build();

        // Restore group states from persistence
        if (_storageService != null)
        {
            await RestoreGroupStatesAsync();
        }

        _logger.LogInformation("Managed MLS service initialized successfully");

        // Save service state (only writes new keys if we generated them)
        if (!restoredKeys)
        {
            await SaveServiceStateAsync();
        }
    }

    public async Task<KeyPackage> GenerateKeyPackageAsync()
    {
        EnsureInitialized();

        // Call MlsGroup.CreateKeyPackage directly to capture initPriv/hpkePriv
        var mlsKp = MlsGroup.CreateKeyPackage(
            _cipherSuite, _identity!, _signingPrivateKey!, _signingPublicKey!,
            out var initPrivateKey, out var hpkePrivateKey);

        // Store for later ProcessWelcomeAsync
        byte[] kpBytes = TlsCodec.Serialize(writer => mlsKp.WriteTo(writer));
        _storedKeyPackageBytes = kpBytes;
        _storedInitPrivateKey = initPrivateKey;
        _storedHpkePrivateKey = hpkePrivateKey;

        // Build Nostr event tags using the protocol builder
        var (content, tags) = KeyPackageEventBuilder.BuildKeyPackageEvent(
            kpBytes, _publicKeyHex!, Array.Empty<string>());

        // Convert string[][] tags to List<List<string>> for OpenChat's KeyPackage model
        var nostrTags = tags.Select(t => t.ToList()).ToList();

        var keyPackage = KeyPackage.Create(_publicKeyHex!, kpBytes, 0x0001);
        keyPackage.NostrTags = nostrTags;

        _logger.LogInformation(
            "Generated KeyPackage with {TagCount} tags, {Len} bytes (managed)",
            nostrTags.Count, kpBytes.Length);

        await SaveServiceStateAsync();

        return keyPackage;
    }

    public async Task<MlsGroupInfo> CreateGroupAsync(string groupName)
    {
        EnsureInitialized();

        _logger.LogInformation("CreateGroup: creating MLS group '{GroupName}' (managed)", groupName);

        var result = await _mdk!.CreateGroupAsync(
            _identity!, _signingPrivateKey!, _signingPublicKey!,
            groupName, Array.Empty<string>());

        var groupId = result.Group.Id.Value;
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();

        _logger.LogInformation("CreateGroup: created group {GroupId}, epoch={Epoch} (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], result.Group.Epoch);

        await SaveGroupStateAsync(groupId);

        return new MlsGroupInfo
        {
            GroupId = groupId,
            GroupName = groupName,
            Epoch = result.Group.Epoch,
            MemberPublicKeys = new List<string> { _publicKeyHex! }
        };
    }

    public async Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(keyPackage.EventJson))
            throw new InvalidOperationException("KeyPackage is missing the Nostr event JSON required for MLS processing");

        // Extract raw KeyPackage bytes from the event JSON content.
        // The content is base64-encoded regardless of whether the encoding tag says
        // "base64" (Rust MDK) or "mls-base64" (C# MDK) — both are standard base64.
        byte[] kpBytes;
        try
        {
            using var doc = JsonDocument.Parse(keyPackage.EventJson);
            var root = doc.RootElement;

            var content = root.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("KeyPackage event has null content");

            kpBytes = Convert.FromBase64String(content);
            if (kpBytes.Length == 0)
                throw new InvalidOperationException("KeyPackage content decoded to empty bytes");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse KeyPackage event JSON", ex);
        }

        var result = await _mdk!.AddMembersAsync(groupId, new[] { kpBytes });

        await SaveGroupStateAsync(groupId);

        return new MlsWelcome
        {
            WelcomeData = result.WelcomeBytes ?? Array.Empty<byte>(),
            CommitData = result.CommitMessageBytes,
            RecipientPublicKey = keyPackage.OwnerPublicKey,
            KeyPackageEventId = keyPackage.NostrEventId
        };
    }

    public async Task<MlsGroupInfo> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId)
    {
        EnsureInitialized();

        if (_storedKeyPackageBytes == null || _storedInitPrivateKey == null || _storedHpkePrivateKey == null)
            throw new InvalidOperationException("No stored KeyPackage data. Call GenerateKeyPackageAsync first.");

        _logger.LogInformation("ProcessWelcomeAsync: wrapperEventId={EventId}, welcomeData={Len} bytes (managed)",
            wrapperEventId[..Math.Min(16, wrapperEventId.Length)], welcomeData.Length);

        // The welcome data may be either:
        // 1. Raw TLS bytes (from managed/C# MDK sender) — ready to use directly
        // 2. UTF-8 JSON (from Rust/native MDK sender) — a Nostr rumor event (or array)
        //    where the actual MLS Welcome binary is base64-encoded in the "content" field
        var mlsWelcomeBytes = ExtractMlsWelcomeBytes(welcomeData);

        var preview = await _mdk!.PreviewWelcomeAsync(
            mlsWelcomeBytes,
            _storedKeyPackageBytes,
            _storedInitPrivateKey,
            _storedHpkePrivateKey,
            _signingPrivateKey!);

        var group = await _mdk.AcceptWelcomeAsync(
            preview.WelcomeId,
            _storedKeyPackageBytes,
            _storedInitPrivateKey,
            _storedHpkePrivateKey,
            _signingPrivateKey!);

        await SaveGroupStateAsync(preview.GroupId);

        return new MlsGroupInfo
        {
            GroupId = preview.GroupId,
            GroupName = preview.GroupName,
            Epoch = group.Epoch,
            MemberPublicKeys = preview.MemberIdentities.ToList()
        };
    }

    public async Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("EncryptMessage: group={GroupId}, plaintext length={Len} (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], plaintext.Length);

        var result = await _mdk!.CreateMessageAsync(groupId, Encoding.UTF8.GetBytes(plaintext));

        _logger.LogDebug("EncryptMessage: produced {Len} bytes ciphertext (managed)", result.Length);

        await SaveGroupStateAsync(groupId);

        return result;
    }

    public async Task<MlsDecryptedMessage> DecryptMessageAsync(byte[] groupId, byte[] ciphertext)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("DecryptMessage: group={GroupId}, ciphertext={Len} bytes (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], ciphertext.Length);

        // Use a synthetic event ID for deduplication
        var eventId = Guid.NewGuid().ToString("N");
        var result = await _mdk!.ProcessMessageAsync(groupId, ciphertext, eventId);

        if (result is ApplicationMessageResult appMsg)
        {
            var senderHex = Convert.ToHexString(appMsg.Message.SenderIdentity);
            var plaintext = Encoding.UTF8.GetString(appMsg.Message.Content);

            _logger.LogDebug("DecryptMessage: sender={Sender}, epoch={Epoch}, plaintext length={Len} (managed)",
                senderHex[..Math.Min(16, senderHex.Length)], appMsg.Message.Epoch, plaintext.Length);

            await SaveGroupStateAsync(groupId);

            return new MlsDecryptedMessage
            {
                SenderPublicKey = senderHex,
                Plaintext = plaintext,
                Epoch = appMsg.Message.Epoch
            };
        }

        throw new InvalidOperationException($"Expected ApplicationMessageResult but got {result.GetType().Name}");
    }

    public async Task ProcessCommitAsync(byte[] groupId, byte[] commitData)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogInformation("ProcessCommit: group={GroupId}, commit={Len} bytes (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], commitData.Length);

        var eventId = Guid.NewGuid().ToString("N");
        var result = await _mdk!.ProcessMessageAsync(groupId, commitData, eventId);

        if (result is CommitResult)
        {
            _logger.LogInformation("ProcessCommit: success for group {GroupId} (managed)",
                groupIdHex[..Math.Min(16, groupIdHex.Length)]);
            await SaveGroupStateAsync(groupId);
            return;
        }

        if (result is UnprocessableResult unprocessable)
        {
            throw new InvalidOperationException($"Failed to process commit: {unprocessable.Reason}");
        }
    }

    public async Task<byte[]> UpdateKeysAsync(byte[] groupId)
    {
        EnsureInitialized();

        var result = await _mdk!.SelfUpdateAsync(groupId);

        await SaveGroupStateAsync(groupId);

        return result.CommitMessageBytes;
    }

    public async Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey)
    {
        EnsureInitialized();

        // Find the leaf index for this member's identity
        var members = await _mdk!.GetMembersAsync(groupId);
        var memberHexUpper = memberPublicKey.ToUpperInvariant();
        var member = members.FirstOrDefault(m =>
            m.identityHex.Equals(memberHexUpper, StringComparison.OrdinalIgnoreCase));

        if (member.identityHex == null)
            throw new InvalidOperationException($"Member {memberPublicKey} not found in group");

        var result = await _mdk.RemoveMembersAsync(groupId, new[] { member.leafIndex });

        await SaveGroupStateAsync(groupId);

        return result.CommitMessageBytes;
    }

    public async Task<MlsGroupInfo?> GetGroupInfoAsync(byte[] groupId)
    {
        EnsureInitialized();

        var group = await _mdk!.GetGroupAsync(groupId);
        if (group == null) return null;

        var members = await _mdk.GetMembersAsync(groupId);

        return new MlsGroupInfo
        {
            GroupId = groupId,
            GroupName = group.Name,
            Epoch = group.Epoch,
            MemberPublicKeys = members.Select(m => m.identityHex).ToList()
        };
    }

    public Task<byte[]> ExportGroupStateAsync(byte[] groupId)
    {
        EnsureInitialized();

        var stateBytes = _mdk!.ExportGroupState(groupId);
        return Task.FromResult(stateBytes);
    }

    public Task ImportGroupStateAsync(byte[] groupId, byte[] state)
    {
        EnsureInitialized();

        _mdk!.ImportGroupState(groupId, state);
        _logger.LogInformation("Imported MLS group state for {GroupId}", Convert.ToHexString(groupId).ToLowerInvariant());
        return Task.CompletedTask;
    }

    public Task<byte[]?> ExportServiceStateAsync()
    {
        if (_signingPrivateKey == null || _signingPublicKey == null)
            return Task.FromResult<byte[]?>(null);

        var stateBytes = TlsCodec.Serialize(writer =>
        {
            writer.WriteUint8(ServiceStateVersion);
            writer.WriteOpaqueV(_signingPrivateKey);
            writer.WriteOpaqueV(_signingPublicKey);

            // Stored KeyPackage data (may be null)
            byte hasKp = (byte)(_storedKeyPackageBytes != null ? 1 : 0);
            writer.WriteUint8(hasKp);
            if (_storedKeyPackageBytes != null)
            {
                writer.WriteOpaqueV(_storedKeyPackageBytes);
                writer.WriteOpaqueV(_storedInitPrivateKey!);
                writer.WriteOpaqueV(_storedHpkePrivateKey!);
            }
        });

        return Task.FromResult<byte[]?>(stateBytes);
    }

    public Task ImportServiceStateAsync(byte[] state)
    {
        var reader = new TlsReader(state);
        byte version = reader.ReadUint8();
        if (version != ServiceStateVersion)
            throw new InvalidOperationException($"Unsupported service state version: {version}");

        _signingPrivateKey = reader.ReadOpaqueV();
        _signingPublicKey = reader.ReadOpaqueV();

        byte hasKp = reader.ReadUint8();
        if (hasKp != 0)
        {
            _storedKeyPackageBytes = reader.ReadOpaqueV();
            _storedInitPrivateKey = reader.ReadOpaqueV();
            _storedHpkePrivateKey = reader.ReadOpaqueV();
        }

        _logger.LogInformation("Restored MLS service state (signingKey={Len} bytes, hasKeyPackage={HasKp})",
            _signingPrivateKey.Length, hasKp != 0);
        return Task.CompletedTask;
    }

    // ---- Persistence helpers ----

    private async Task RestoreGroupStatesAsync()
    {
        if (_storageService == null || _mdk == null) return;

        try
        {
            var chats = await _storageService.GetAllChatsAsync();
            var restored = 0;

            foreach (var chat in chats)
            {
                if (chat.MlsGroupId == null) continue;

                var hex = Convert.ToHexString(chat.MlsGroupId).ToLowerInvariant();
                try
                {
                    var state = await _storageService.GetMlsStateAsync(hex);
                    if (state != null)
                    {
                        _mdk.ImportGroupState(chat.MlsGroupId, state);
                        restored++;
                        _logger.LogDebug("Restored MLS group state for {GroupId}", hex[..Math.Min(16, hex.Length)]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore MLS state for group {GroupId}", hex[..Math.Min(16, hex.Length)]);
                }
            }

            _logger.LogInformation("Restored {Count} MLS group states from persistence", restored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore MLS group states");
        }
    }

    private async Task SaveGroupStateAsync(byte[] groupId)
    {
        if (_storageService == null) return;

        try
        {
            var stateBytes = _mdk!.ExportGroupState(groupId);
            var hex = Convert.ToHexString(groupId).ToLowerInvariant();
            await _storageService.SaveMlsStateAsync(hex, stateBytes);
            _logger.LogDebug("Saved MLS state for group {GroupId} ({Len} bytes)", hex[..Math.Min(16, hex.Length)], stateBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save MLS state for group {GroupId}",
                Convert.ToHexString(groupId).ToLowerInvariant());
        }
    }

    private async Task SaveServiceStateAsync()
    {
        if (_storageService == null) return;

        try
        {
            var state = await ExportServiceStateAsync();
            if (state != null)
            {
                await _storageService.SaveMlsStateAsync(ServiceStateKey, state);
                _logger.LogDebug("Saved MLS service state ({Len} bytes)", state.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save MLS service state");
        }
    }

    /// <summary>
    /// Detects whether welcome data is JSON (from Rust/native MDK) or raw TLS bytes (from managed MDK),
    /// and extracts the raw MLS Welcome TLS bytes in either case.
    /// </summary>
    private byte[] ExtractMlsWelcomeBytes(byte[] welcomeData)
    {
        if (welcomeData.Length == 0)
            throw new InvalidOperationException("Welcome data is empty.");

        // Raw TLS MLS Welcome starts with 0x00..0x0F (version/cipher_suite fields).
        // JSON starts with '{' (0x7B) or '[' (0x5B).
        byte first = welcomeData[0];
        if (first != (byte)'{' && first != (byte)'[')
        {
            _logger.LogDebug("ProcessWelcome: welcome data appears to be raw TLS ({Len} bytes)", welcomeData.Length);
            return welcomeData;
        }

        // Rust/native MDK welcome: JSON rumor event (or array of rumor events)
        // The rumor event's "content" field contains the MLS Welcome as base64.
        _logger.LogInformation("ProcessWelcome: detected JSON welcome data from Rust/native MDK, extracting MLS Welcome bytes");

        try
        {
            var json = Encoding.UTF8.GetString(welcomeData);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // May be an array of rumor events — extract the first one
            JsonElement rumorEvent;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    throw new InvalidOperationException("Welcome JSON array is empty.");
                rumorEvent = root[0];
                _logger.LogDebug("ProcessWelcome: extracted first rumor event from array of {Count}", root.GetArrayLength());
            }
            else
            {
                rumorEvent = root;
            }

            // Extract the "content" field which contains base64-encoded MLS Welcome
            var content = rumorEvent.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("Rumor event has null content field.");

            var mlsBytes = Convert.FromBase64String(content);
            if (mlsBytes.Length == 0)
                throw new InvalidOperationException("Rumor event content decoded to empty bytes.");

            _logger.LogInformation(
                "ProcessWelcome: extracted {Len} bytes of raw MLS Welcome from JSON rumor event",
                mlsBytes.Length);

            return mlsBytes;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to extract MLS Welcome bytes from Rust/native MDK JSON welcome data", ex);
        }
    }

    private void EnsureInitialized()
    {
        if (_mdk == null)
            throw new InvalidOperationException("MLS service not initialized. Call InitializeAsync first.");
    }
}

#endif
