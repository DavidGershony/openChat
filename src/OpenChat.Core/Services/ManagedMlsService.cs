using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using MarmotCs.Core;
using MarmotCs.Core.Results;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using MarmotCs.Protocol.Mip00;
using MarmotCs.Storage.Memory;

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
    private string? _privateKeyHex;
    private byte[]? _identity;
    private byte[]? _signingPrivateKey;
    private byte[]? _signingPublicKey;

    /// <summary>
    /// Private key material for a stored KeyPackage.
    /// MLS allows multiple KeyPackages per user (RFC 9420 Section 16.8).
    /// Each can only be used for one Welcome, so we keep several available.
    /// </summary>
    private class StoredKeyPackageMaterial
    {
        public byte[] KeyPackageBytes { get; init; } = Array.Empty<byte>();
        public byte[] InitPrivateKey { get; init; } = Array.Empty<byte>();
        public byte[] HpkePrivateKey { get; init; } = Array.Empty<byte>();
    }

    // All stored KeyPackages with their private keys (supports multiple)
    private readonly List<StoredKeyPackageMaterial> _storedKeyPackages = new();

    // Optional external signer for kind 445 events (Amber / NIP-46)
    private INostrEventSigner? _nostrEventSigner;

    // Prevents concurrent InitializeAsync calls from racing
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const string ServiceStateKey = "__service__";
    private const byte ServiceStateVersion = 2;
    private const byte ServiceStateVersion1 = 1;

    public ManagedMlsService(IStorageService? storageService = null)
    {
        _storageService = storageService;
        _logger = LoggingConfiguration.CreateLogger<ManagedMlsService>();
        _logger.LogInformation("ManagedMlsService instance created (C# MDK backend, persistence={HasStorage})",
            storageService != null);
    }

    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        await _initLock.WaitAsync();
        try
        {
            // Guard against double-initialization clobbering restored state
            if (_mdk != null)
            {
                _logger.LogDebug("ManagedMlsService already initialized, skipping");
                return;
            }

            _logger.LogInformation("Initializing managed MLS service");
            _publicKeyHex = publicKeyHex;
            _privateKeyHex = privateKeyHex;
            _identity = Convert.FromHexString(publicKeyHex);

            // Try to restore signing keys and KeyPackages from persisted service state first
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
                        _logger.LogInformation("Restored MLS service state from persistence (signingKey={HasKey}, storedKeyPackages={Count})",
                            restoredKeys, _storedKeyPackages.Count);
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
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<KeyPackage> GenerateKeyPackageAsync()
    {
        EnsureInitialized();

        // Call MlsGroup.CreateKeyPackage directly to capture initPriv/hpkePriv.
        // Advertise support for required extensions:
        //   0x000A = LastResort (RFC 9420 Section 17.3) — required by Rust MDK
        //   0xF2EE = NostrGroupData (marmot extension for group metadata)
        var mlsKp = MlsGroup.CreateKeyPackage(
            _cipherSuite, _identity!, _signingPrivateKey!, _signingPublicKey!,
            out var initPrivateKey, out var hpkePrivateKey,
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE });

        // Store for later ProcessWelcomeAsync (add to list, don't overwrite)
        byte[] kpBytes = TlsCodec.Serialize(writer => mlsKp.WriteTo(writer));
        _storedKeyPackages.Add(new StoredKeyPackageMaterial
        {
            KeyPackageBytes = kpBytes,
            InitPrivateKey = initPrivateKey,
            HpkePrivateKey = hpkePrivateKey
        });

        // Build Nostr event tags using the protocol builder
        var (content, tags) = KeyPackageEventBuilder.BuildKeyPackageEvent(
            kpBytes, _publicKeyHex!, Array.Empty<string>(),
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE });

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

        // Extract raw KeyPackage bytes from the base64-encoded event content.
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

        // Capture the pre-commit exporter secret BEFORE AddMembersAsync advances the epoch.
        // MIP-03: commits must be encrypted with the current epoch's exporter secret
        // so that existing members (at the current epoch) can decrypt them.
        var preCommitExporterSecret = _mdk!.GetExporterSecret(groupId);

        var result = await _mdk!.AddMembersAsync(groupId, new[] { kpBytes });

        await SaveGroupStateAsync(groupId);

        // MIP-02 requires the Welcome to be wrapped in an MLSMessage container:
        //   MLSMessage { version=mls10(0x0001), wire_format=mls_welcome(0x0003), Welcome }
        // The C# MDK produces raw Welcome TLS bytes, so we prepend the 4-byte header.
        var rawWelcome = result.WelcomeBytes ?? Array.Empty<byte>();
        var mlsMessage = WrapWelcomeInMlsMessage(rawWelcome);

        // MIP-03 encrypt the commit with the PRE-commit exporter secret
        var mip03Encrypted = Mip03Crypto.Encrypt(preCommitExporterSecret, result.CommitMessageBytes);

        return new MlsWelcome
        {
            WelcomeData = mlsMessage,
            CommitData = mip03Encrypted,  // Now MIP-03 encrypted, not raw
            RecipientPublicKey = keyPackage.OwnerPublicKey,
            KeyPackageEventId = keyPackage.NostrEventId
        };
    }

    public async Task<MlsGroupInfo> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId)
    {
        EnsureInitialized();

        if (_storedKeyPackages.Count == 0)
            throw new InvalidOperationException("No stored KeyPackage data. Call GenerateKeyPackageAsync first.");

        _logger.LogInformation("ProcessWelcomeAsync: wrapperEventId={EventId}, welcomeData={Len} bytes, {KpCount} stored KeyPackages (managed)",
            wrapperEventId[..Math.Min(16, wrapperEventId.Length)], welcomeData.Length, _storedKeyPackages.Count);

        // The welcome data may be either:
        // 1. Raw TLS bytes (from managed/C# MDK sender) — ready to use directly
        // 2. UTF-8 JSON (from Rust/native MDK sender) — a Nostr rumor event (or array)
        //    where the actual MLS Welcome binary is base64-encoded in the "content" field
        var mlsWelcomeBytes = ExtractMlsWelcomeBytes(welcomeData);
        _logger.LogInformation("ProcessWelcome: extracted MLS bytes ({Len} bytes), first 16: {Hex}",
            mlsWelcomeBytes.Length,
            Convert.ToHexString(mlsWelcomeBytes[..Math.Min(16, mlsWelcomeBytes.Length)]));

        // Try each stored KeyPackage — the Welcome is encrypted to one specific KeyPackage.
        // MLS allows clients to publish multiple KeyPackages (RFC 9420 Section 16.8).
        // Randomize attempt order to prevent timing side-channel leaking which index matched.
        Exception? lastError = null;
        var indices = Enumerable.Range(0, _storedKeyPackages.Count).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        foreach (var i in indices)
        {
            var kp = _storedKeyPackages[i];
            try
            {
                _logger.LogDebug("ProcessWelcome: trying stored KeyPackage {Index}/{Total} ({Len} bytes)",
                    i + 1, _storedKeyPackages.Count, kp.KeyPackageBytes.Length);

                var preview = await _mdk!.PreviewWelcomeAsync(
                    mlsWelcomeBytes,
                    kp.KeyPackageBytes,
                    kp.InitPrivateKey,
                    kp.HpkePrivateKey,
                    _signingPrivateKey!);

                var group = await _mdk.AcceptWelcomeAsync(
                    preview.WelcomeId,
                    kp.KeyPackageBytes,
                    kp.InitPrivateKey,
                    kp.HpkePrivateKey,
                    _signingPrivateKey!);

                _logger.LogInformation("ProcessWelcome: matched stored KeyPackage {Index}/{Total}",
                    i + 1, _storedKeyPackages.Count);

                // Remove the used KeyPackage — each can only be used once
                _storedKeyPackages.RemoveAt(i);
                await SaveServiceStateAsync();
                await SaveGroupStateAsync(preview.GroupId);

                return new MlsGroupInfo
                {
                    GroupId = preview.GroupId,
                    GroupName = preview.GroupName,
                    Epoch = group.Epoch,
                    MemberPublicKeys = preview.MemberIdentities.ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug("ProcessWelcome: KeyPackage {Index} did not match: {Error}",
                    i + 1, ex.Message);
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"None of the {_storedKeyPackages.Count} stored KeyPackages match this Welcome. " +
            "The private key material for the targeted KeyPackage may have been lost.",
            lastError);
    }

    public async Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("EncryptMessage: group={GroupId}, plaintext length={Len} (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], plaintext.Length);

        // Match the Rust MDK flow:
        // 1. Wrap plaintext in a Nostr rumor event (kind 9)
        // 2. MLS-encrypt the rumor → raw TLS bytes
        // 3. MIP-03 encrypt with exporter_secret (ChaCha20-Poly1305)
        // 4. Build a signed Nostr event (kind 445) with base64 content and h-tag

        // Step 1: Create rumor event JSON (same as Rust: kind 9, pubkey, content)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rumorId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var escapedContent = JsonSerializer.Serialize(plaintext);
        var rumorJson = $"{{\"id\":\"{rumorId}\",\"pubkey\":\"{_publicKeyHex}\",\"created_at\":{now},\"kind\":9,\"tags\":[],\"content\":{escapedContent}}}";

        // Step 2: MLS-encrypt the rumor
        var mlsBytes = await _mdk!.CreateMessageAsync(groupId, Encoding.UTF8.GetBytes(rumorJson));

        // Step 3: MIP-03 ChaCha20-Poly1305 encryption
        var exporterSecret = _mdk!.GetExporterSecret(groupId);
        var mip03Encrypted = Mip03Crypto.Encrypt(exporterSecret, mlsBytes);
        var base64Content = Convert.ToBase64String(mip03Encrypted);

        // Step 4: Build signed kind 445 Nostr event with EPHEMERAL key (MIP-03 privacy)
        // MIP-03: kind 445 events SHOULD use ephemeral keys so relay operators
        // cannot link group messages to user identities. The sender identity is
        // inside the MLS-encrypted rumor, not in the Nostr event pubkey.
        var nostrGroupId = _mdk!.GetNostrGroupId(groupId);
        var hTagValue = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;
        var tags = new List<List<string>> { new() { "h", hTagValue }, new() { "encoding", "base64" } };
        var eventJson = BuildEphemeralSignedEvent(445, base64Content, tags);

        _logger.LogDebug("EncryptMessage: produced {Len} bytes event JSON (managed)", eventJson.Length);

        await SaveGroupStateAsync(groupId);

        return eventJson;
    }

    public async Task<MlsDecryptedMessage> DecryptMessageAsync(byte[] groupId, byte[] ciphertext)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("DecryptMessage: group={GroupId}, ciphertext={Len} bytes (managed)",
            groupIdHex[..Math.Min(16, groupIdHex.Length)], ciphertext.Length);

        // Ciphertext may be:
        // 1. A JSON Nostr event (from our own EncryptMessageAsync) with base64 content
        // 2. Raw MIP-03 ChaCha20-Poly1305 encrypted bytes (from web app / relay)
        // 3. Raw MLS PrivateMessage bytes (internal tests)
        bool isFromNostrEvent = ciphertext.Length > 0 && (ciphertext[0] == (byte)'{' || ciphertext[0] == (byte)'[');
        var payloadBytes = ExtractMlsMessageBytes(ciphertext);

        // MIP-03: All relay messages (both JSON events and raw bytes) are
        // ChaCha20-Poly1305 encrypted with the group's MLS exporter secret.
        // Always apply MIP-03 decryption unless the bytes are already a valid MLS message
        // (starts with PrivateMessage header: version 0x0001, wire_format 0x0002).
        bool isMlsPrivateMessage = payloadBytes.Length >= 4 &&
            payloadBytes[0] == 0x00 && payloadBytes[1] == 0x01 &&
            payloadBytes[2] == 0x00 && payloadBytes[3] == 0x02;

        byte[] mlsBytes;
        if (!isMlsPrivateMessage)
        {
            _logger.LogDebug("DecryptMessage: applying MIP-03 ChaCha20-Poly1305 decryption ({Len} bytes)",
                payloadBytes.Length);
            var exporterSecret = _mdk!.GetExporterSecret(groupId);
            mlsBytes = Mip03Crypto.Decrypt(exporterSecret, payloadBytes);
            _logger.LogDebug("DecryptMessage: MIP-03 decrypted to {Len} bytes, first 32: {Hex}",
                mlsBytes.Length,
                Convert.ToHexString(mlsBytes[..Math.Min(32, mlsBytes.Length)]));
        }
        else
        {
            mlsBytes = payloadBytes;
        }

        // Use a synthetic event ID for deduplication
        var eventId = Guid.NewGuid().ToString("N");
        var result = await _mdk!.ProcessMessageAsync(groupId, mlsBytes, eventId);

        if (result is ApplicationMessageResult appMsg)
        {
            var senderHex = Convert.ToHexString(appMsg.Message.SenderIdentity);
            var plaintext = Encoding.UTF8.GetString(appMsg.Message.Content);

            // The Rust MDK wraps messages as Nostr rumor events (JSON with "content" field).
            // Extract the actual message text from the rumor event if present.
            // Also parse imeta tags for image messages (MIP-04).
            string? imageUrl = null;
            string? mediaType = null;
            string? fileName = null;
            string? fileSha256 = null;
            string? encryptionNonce = null;
            string? encryptionVersion = null;

            if (plaintext.Length > 0 && plaintext[0] == '{')
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(plaintext);
                    if (doc.RootElement.TryGetProperty("content", out var contentProp))
                    {
                        var extracted = contentProp.GetString();
                        if (extracted != null)
                        {
                            _logger.LogDebug("DecryptMessage: extracted content from rumor event ({RumorLen} bytes → {ContentLen} chars)",
                                plaintext.Length, extracted.Length);
                            plaintext = extracted;
                        }
                    }

                    // Parse imeta tags for image metadata (MIP-04)
                    if (doc.RootElement.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var tag in tagsProp.EnumerateArray())
                        {
                            if (tag.GetArrayLength() < 2) continue;
                            var tagName = tag[0].GetString();
                            if (tagName != "imeta") continue;

                            _logger.LogDebug("DecryptMessage: found imeta tag with {Count} entries", tag.GetArrayLength() - 1);

                            // Parse imeta entries: each element after "imeta" is "key value"
                            for (var i = 1; i < tag.GetArrayLength(); i++)
                            {
                                var entry = tag[i].GetString();
                                if (string.IsNullOrEmpty(entry)) continue;

                                if (entry.StartsWith("url "))
                                    imageUrl = entry.Substring(4);
                                else if (entry.StartsWith("m "))
                                    mediaType = entry.Substring(2);
                                else if (entry.StartsWith("filename "))
                                    fileName = entry.Substring(9);
                                else if (entry.StartsWith("x "))
                                    fileSha256 = entry.Substring(2);
                                else if (entry.StartsWith("n ") && entry.Length <= 26)
                                    encryptionNonce = entry.Substring(2); // MIP-04 v2: "n <24hex>"
                                else if (entry.StartsWith("nonce "))
                                    encryptionNonce = entry.Substring(6);
                                else if (entry.StartsWith("v "))
                                    encryptionVersion = entry.Substring(2); // MIP-04 v2: "v mip04-v2"
                                else if (entry.StartsWith("encryption-version "))
                                    encryptionVersion = entry.Substring(19);
                            }

                            if (imageUrl != null)
                            {
                                _logger.LogInformation("DecryptMessage: extracted imeta - url={Url}, type={MediaType}, filename={FileName}, sha256={Sha256}, nonce={Nonce}, version={Version}",
                                    imageUrl, mediaType ?? "(none)", fileName ?? "(none)",
                                    fileSha256?[..Math.Min(16, fileSha256?.Length ?? 0)] ?? "(none)",
                                    encryptionNonce?[..Math.Min(16, encryptionNonce?.Length ?? 0)] ?? "(none)",
                                    encryptionVersion ?? "(none)");
                            }

                            break; // Only process the first imeta tag
                        }
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogDebug(ex, "DecryptMessage: plaintext starts with '{{' but is not valid JSON, using raw value");
                }
            }

            _logger.LogDebug("DecryptMessage: sender={Sender}, epoch={Epoch}, plaintext length={Len} (managed)",
                senderHex[..Math.Min(16, senderHex.Length)], appMsg.Message.Epoch, plaintext.Length);

            await SaveGroupStateAsync(groupId);

            return new MlsDecryptedMessage
            {
                SenderPublicKey = senderHex,
                Plaintext = plaintext,
                Epoch = appMsg.Message.Epoch,
                ImageUrl = imageUrl,
                MediaType = mediaType,
                FileName = fileName,
                FileSha256 = fileSha256,
                EncryptionNonce = encryptionNonce,
                EncryptionVersion = encryptionVersion
            };
        }

        var reason = result is UnprocessableResult ur ? ur.Reason : result.GetType().Name;
        throw new InvalidOperationException($"Expected ApplicationMessageResult but got {result.GetType().Name}: {reason}");
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

            // Write count of stored KeyPackages, then each one
            writer.WriteUint16((ushort)_storedKeyPackages.Count);
            foreach (var kp in _storedKeyPackages)
            {
                writer.WriteOpaqueV(kp.KeyPackageBytes);
                writer.WriteOpaqueV(kp.InitPrivateKey);
                writer.WriteOpaqueV(kp.HpkePrivateKey);
            }
        });

        return Task.FromResult<byte[]?>(stateBytes);
    }

    public Task ImportServiceStateAsync(byte[] state)
    {
        var reader = new TlsReader(state);
        byte version = reader.ReadUint8();

        _signingPrivateKey = reader.ReadOpaqueV();
        _signingPublicKey = reader.ReadOpaqueV();

        _storedKeyPackages.Clear();

        if (version == ServiceStateVersion1)
        {
            // v1 format: single optional KeyPackage
            byte hasKp = reader.ReadUint8();
            if (hasKp != 0)
            {
                _storedKeyPackages.Add(new StoredKeyPackageMaterial
                {
                    KeyPackageBytes = reader.ReadOpaqueV(),
                    InitPrivateKey = reader.ReadOpaqueV(),
                    HpkePrivateKey = reader.ReadOpaqueV()
                });
            }
        }
        else if (version == ServiceStateVersion)
        {
            // v2 format: list of KeyPackages
            ushort count = reader.ReadUint16();
            for (int i = 0; i < count; i++)
            {
                _storedKeyPackages.Add(new StoredKeyPackageMaterial
                {
                    KeyPackageBytes = reader.ReadOpaqueV(),
                    InitPrivateKey = reader.ReadOpaqueV(),
                    HpkePrivateKey = reader.ReadOpaqueV()
                });
            }
        }
        else
        {
            throw new InvalidOperationException($"Unsupported service state version: {version}");
        }

        _logger.LogInformation("Restored MLS service state (signingKey={Len} bytes, storedKeyPackages={Count})",
            _signingPrivateKey.Length, _storedKeyPackages.Count);
        return Task.CompletedTask;
    }

    public byte[]? GetNostrGroupId(byte[] groupId)
    {
        EnsureInitialized();
        return _mdk!.GetNostrGroupId(groupId);
    }

    public int GetStoredKeyPackageCount() => _storedKeyPackages.Count;

    public bool HasKeyMaterialForKeyPackage(byte[] keyPackageData)
    {
        return _storedKeyPackages.Any(kp =>
            kp.KeyPackageBytes.AsSpan().SequenceEqual(keyPackageData.AsSpan()));
    }

    public void SetNostrEventSigner(INostrEventSigner signer)
    {
        _nostrEventSigner = signer;
        _logger.LogInformation("Nostr event signer set: {SignerType}", signer.GetType().Name);
    }

    public byte[] GetMediaExporterSecret(byte[] groupId)
    {
        EnsureInitialized();

        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("GetMediaExporterSecret: group={GroupId}", groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        // Access the private _groups dictionary in Mdk via reflection to call ExportSecret
        // with MIP-04 parameters (label="marmot", context="encrypted-media", length=32).
        // The public GetExporterSecret only supports MIP-03 hardcoded parameters.
        var groupsField = _mdk!.GetType().GetField("_groups",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (groupsField == null)
            throw new InvalidOperationException("Cannot access MLS group state for MIP-04 export (reflection failed)");

        var groups = groupsField.GetValue(_mdk) as System.Collections.IDictionary;
        if (groups == null)
            throw new InvalidOperationException("MLS groups dictionary is null");

        var groupHexKey = Convert.ToHexString(groupId);
        if (!groups.Contains(groupHexKey))
            throw new InvalidOperationException($"MLS group {groupIdHex[..Math.Min(16, groupIdHex.Length)]} not found");

        var mlsGroup = groups[groupHexKey];
        var exportMethod = mlsGroup!.GetType().GetMethod("ExportSecret",
            new[] { typeof(string), typeof(byte[]), typeof(int) });

        if (exportMethod == null)
            throw new InvalidOperationException("Cannot find ExportSecret method on MlsGroup");

        var mediaContext = Encoding.UTF8.GetBytes("encrypted-media");
        var result = exportMethod.Invoke(mlsGroup, new object[] { "marmot", mediaContext, 32 }) as byte[];

        if (result == null || result.Length != 32)
            throw new InvalidOperationException("MIP-04 ExportSecret returned invalid result");

        _logger.LogInformation("GetMediaExporterSecret: derived 32-byte secret for group {GroupId}",
            groupIdHex[..Math.Min(16, groupIdHex.Length)]);

        return result;
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
    /// Extracts raw MLS Welcome TLS bytes from whatever format the welcome data arrives in.
    /// Welcome data can be:
    /// 1. MLSMessage-wrapped TLS bytes (from any MIP-02 compliant sender) — strip 4-byte header
    /// 2. Raw Welcome TLS bytes (legacy C# MDK) — use directly
    /// 3. UTF-8 JSON rumor event (from Rust/native MDK via MarmotWrapper) — extract base64 content
    /// The C# MDK's internal parser expects raw Welcome bytes (without MLSMessage header).
    /// </summary>
    private byte[] ExtractMlsWelcomeBytes(byte[] welcomeData)
    {
        if (welcomeData.Length == 0)
            throw new InvalidOperationException("Welcome data is empty.");

        byte first = welcomeData[0];

        // JSON starts with '{' (0x7B) or '[' (0x5B) — Rust/native MDK rumor event format
        if (first == (byte)'{' || first == (byte)'[')
        {
            _logger.LogInformation("ProcessWelcome: detected JSON welcome data from Rust/native MDK, extracting MLS Welcome bytes");
            return ExtractFromJsonRumor(welcomeData);
        }

        // Binary TLS data — check if it's MLSMessage-wrapped (MIP-02 compliant)
        // MLSMessage header: version=0x0001 (2 bytes) + wire_format=0x0003 (2 bytes)
        if (welcomeData.Length >= 4 &&
            welcomeData[0] == 0x00 && welcomeData[1] == 0x01 &&  // version = mls10
            welcomeData[2] == 0x00 && welcomeData[3] == 0x03)    // wire_format = mls_welcome
        {
            _logger.LogDebug("ProcessWelcome: stripping MLSMessage header from {Len} bytes", welcomeData.Length);
            return welcomeData[4..]; // Strip the 4-byte MLSMessage header
        }

        // Raw Welcome TLS bytes (legacy format without MLSMessage wrapper)
        _logger.LogDebug("ProcessWelcome: welcome data appears to be raw TLS ({Len} bytes)", welcomeData.Length);
        return welcomeData;
    }

    private byte[] ExtractFromJsonRumor(byte[] welcomeData)
    {
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

            // Extract the "content" field which contains base64-encoded MLSMessage(Welcome)
            var content = rumorEvent.GetProperty("content").GetString()
                ?? throw new InvalidOperationException("Rumor event has null content field.");

            var mlsBytes = Convert.FromBase64String(content);
            if (mlsBytes.Length == 0)
                throw new InvalidOperationException("Rumor event content decoded to empty bytes.");

            _logger.LogInformation(
                "ProcessWelcome: extracted {Len} bytes from JSON rumor event content",
                mlsBytes.Length);

            // The content may be MLSMessage-wrapped — strip the header if present
            if (mlsBytes.Length >= 4 &&
                mlsBytes[0] == 0x00 && mlsBytes[1] == 0x01 &&
                mlsBytes[2] == 0x00 && mlsBytes[3] == 0x03)
            {
                _logger.LogDebug("ProcessWelcome: stripping MLSMessage header from extracted bytes");
                return mlsBytes[4..];
            }

            return mlsBytes;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to extract MLS Welcome bytes from JSON welcome data", ex);
        }
    }

    /// <summary>
    /// Extracts raw MLS message bytes from whatever format the ciphertext arrives in.
    /// Handles JSON Nostr events (from Rust/native MDK) and raw TLS bytes (from C# MDK).
    /// </summary>
    private byte[] ExtractMlsMessageBytes(byte[] ciphertext)
    {
        if (ciphertext.Length == 0)
            return ciphertext;

        byte first = ciphertext[0];

        // JSON object or array — Rust/native MDK Nostr event format
        if (first == (byte)'{' || first == (byte)'[')
        {
            _logger.LogDebug("DecryptMessage: detected JSON message data, extracting MLS bytes");
            try
            {
                var json = Encoding.UTF8.GetString(ciphertext);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var content = root.GetProperty("content").GetString()
                    ?? throw new InvalidOperationException("Message event has null content field.");

                return Convert.FromBase64String(content);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to extract MLS message bytes from JSON event", ex);
            }
        }

        // Raw TLS bytes — pass through
        return ciphertext;
    }

    /// <summary>
    /// Wraps raw Welcome TLS bytes in an MLSMessage container per MIP-02:
    /// MLSMessage { version=mls10(0x0001), wire_format=mls_welcome(0x0003), Welcome }
    /// </summary>
    private static byte[] WrapWelcomeInMlsMessage(byte[] rawWelcome)
    {
        if (rawWelcome.Length == 0) return rawWelcome;

        // Don't double-wrap if already has MLSMessage header
        if (rawWelcome.Length >= 4 &&
            rawWelcome[0] == 0x00 && rawWelcome[1] == 0x01 &&
            rawWelcome[2] == 0x00 && rawWelcome[3] == 0x03)
        {
            return rawWelcome;
        }

        var result = new byte[4 + rawWelcome.Length];
        result[0] = 0x00; result[1] = 0x01; // ProtocolVersion = mls10 (1)
        result[2] = 0x00; result[3] = 0x03; // WireFormat = mls_welcome (3)
        Buffer.BlockCopy(rawWelcome, 0, result, 4, rawWelcome.Length);
        return result;
    }

    /// <summary>
    /// Builds a signed Nostr event JSON (as UTF-8 bytes) matching the Rust MDK output format.
    /// Delegates to INostrEventSigner when available; otherwise falls back to local
    /// NIP-01 serialization, SHA-256 event ID, BIP-340 Schnorr signature.
    /// </summary>
    private async Task<byte[]> BuildSignedNostrEventAsync(int kind, string content, List<List<string>> tags)
    {
        // Delegate to injected signer when available
        if (_nostrEventSigner != null)
        {
            _logger.LogDebug("BuildSignedNostrEventAsync: delegating kind {Kind} signing to {SignerType}",
                kind, _nostrEventSigner.GetType().Name);
            return await _nostrEventSigner.SignEventAsync(kind, content, tags, _publicKeyHex!);
        }

        // Fallback: sign locally with private key (backward compatibility)
        _logger.LogDebug("BuildSignedNostrEventAsync: no signer set, using local private key for kind {Kind}", kind);
        var privateKeyBytes = Convert.FromHexString(_privateKeyHex!);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NIP-01: event ID = SHA256([0,pubkey,created_at,kind,tags,content])
        string serializedForId;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteStringValue(_publicKeyHex);
                writer.WriteNumberValue(createdAt);
                writer.WriteNumberValue(kind);
                writer.WriteStartArray();
                foreach (var tag in tags)
                {
                    writer.WriteStartArray();
                    foreach (var v in tag) writer.WriteStringValue(v);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStringValue(content);
                writer.WriteEndArray();
            }
            serializedForId = Encoding.UTF8.GetString(stream.ToArray());
        }

        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedForId));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // BIP-340 Schnorr signature
        if (!Context.Instance.TryCreateECPrivKey(privateKeyBytes, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Invalid Nostr private key for signing");
        var sig = ecPrivKey.SignBIP340(eventIdBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Build the event JSON object (NOT wrapped in ["EVENT",...] relay message)
        using var eventStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventStream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", _publicKeyHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sigHex);
            writer.WriteEndObject();
        }
        return eventStream.ToArray();
    }

    public Task<byte[]> EncryptCommitAsync(byte[] groupId, byte[] mip03EncryptedCommitData)
    {
        EnsureInitialized();
        var groupIdHex = Convert.ToHexString(groupId).ToLowerInvariant();
        _logger.LogDebug("EncryptCommit: wrapping {Len} bytes MIP-03 encrypted commit in kind 445 event",
            mip03EncryptedCommitData.Length);

        // CommitData is ALREADY MIP-03 encrypted by AddMemberAsync.
        // Just wrap in base64 + ephemeral-signed kind 445 event.
        var base64Content = Convert.ToBase64String(mip03EncryptedCommitData);

        var nostrGroupId = _mdk!.GetNostrGroupId(groupId);
        var hTagValue = nostrGroupId != null
            ? Convert.ToHexString(nostrGroupId).ToLowerInvariant()
            : groupIdHex;
        var tags = new List<List<string>> { new() { "h", hTagValue }, new() { "encoding", "base64" } };
        var eventJson = BuildEphemeralSignedEvent(445, base64Content, tags);

        _logger.LogDebug("EncryptCommit: produced {Len} bytes event JSON", eventJson.Length);
        return Task.FromResult(eventJson);
    }

    /// <summary>
    /// Signs a Nostr event with a randomly generated ephemeral keypair.
    /// Used for kind 445 group messages per MIP-03 to prevent linking messages to user identity.
    /// </summary>
    private byte[] BuildEphemeralSignedEvent(int kind, string content, List<List<string>> tags)
    {
        // Generate ephemeral keypair
        var ephemeralPrivKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(ephemeralPrivKey);
        if (!Context.Instance.TryCreateECPrivKey(ephemeralPrivKey, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Failed to generate ephemeral key");
        var ephemeralPubKey = ecPrivKey.CreateXOnlyPubKey();
        var ephPubBytes = new byte[32];
        ephemeralPubKey.WriteToSpan(ephPubBytes);
        var ephPubHex = Convert.ToHexString(ephPubBytes).ToLowerInvariant();

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NIP-01 event ID
        var serialized = NostrService.SerializeForEventId(ephPubHex, createdAt, kind, tags, content);
        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();

        // BIP-340 Schnorr signature with ephemeral key
        var sig = ecPrivKey.SignBIP340(eventIdBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Build event JSON
        using var eventStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(eventStream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", ephPubHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", sigHex);
            writer.WriteEndObject();
        }

        _logger.LogDebug("BuildEphemeralSignedEvent: kind {Kind}, ephemeral pubkey {PubKey}",
            kind, ephPubHex[..16]);

        return eventStream.ToArray();
    }

    private void EnsureInitialized()
    {
        if (_mdk == null)
            throw new InvalidOperationException("MLS service not initialized. Call InitializeAsync first.");
    }
}
