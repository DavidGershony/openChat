using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Marmot;

/// <summary>
/// High-level C# wrapper for the Marmot native library.
/// Provides safe, async-friendly access to MLS operations.
/// </summary>
public class MarmotWrapper : IDisposable
{
    private readonly ILogger<MarmotWrapper> _logger;
    private IntPtr _client;
    private bool _disposed;
    private bool _initialized;

    // For development/testing before native library is ready
    private readonly Dictionary<string, MockGroup> _mockGroups = new();
    private string? _privateKeyHex;
    private string? _publicKeyHex;

    /// <summary>
    /// Whether the native MLS client is loaded (true) or using mock fallback (false).
    /// </summary>
    public bool IsUsingNativeClient => _client != IntPtr.Zero;

    public MarmotWrapper()
    {
        _logger = LoggingConfiguration.CreateLogger<MarmotWrapper>();
        _logger.LogDebug("MarmotWrapper instance created");
    }

    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        _logger.LogInformation("Initializing MarmotWrapper");
        _logger.LogDebug("Public key: {PubKey}...", publicKeyHex[..16]);

        _privateKeyHex = privateKeyHex;
        _publicKeyHex = publicKeyHex;

        try
        {
            _logger.LogDebug("Attempting to load native Marmot library");
            _client = MarmotInterop.CreateClient(privateKeyHex, publicKeyHex);
            if (_client == IntPtr.Zero)
            {
                var error = GetLastError() ?? "Failed to create Marmot client";
                _logger.LogError("Failed to create native Marmot client: {Error}", error);
                throw new MarmotException(error);
            }
            _logger.LogInformation("Successfully initialized native Marmot client");
            _initialized = true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "Native Marmot library (openchat_native.dll) not found. MLS operations will not work.");
            throw new MarmotException(
                "Native Marmot library (openchat_native.dll) not found. " +
                "Ensure the library is built and placed alongside the application.", ex);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Result of generating a KeyPackage.
    /// </summary>
    public class KeyPackageResult
    {
        /// <summary>
        /// Base64-encoded KeyPackage content.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Raw KeyPackage bytes (decoded from base64).
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Nostr tags provided by MDK for MIP-00 compliance.
        /// </summary>
        public List<List<string>> Tags { get; set; } = new();
    }

    public async Task<KeyPackageResult> GenerateKeyPackageAsync()
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var mockKeyPackage = new byte[256];
            new Random().NextBytes(mockKeyPackage);
            return new KeyPackageResult
            {
                Content = Convert.ToBase64String(mockKeyPackage),
                Data = mockKeyPackage,
                Tags = new List<List<string>>
                {
                    new() { "encoding", "base64" },
                    new() { "mls_protocol_version", "1.0" },
                    new() { "mls_ciphersuite", "0x0001" }
                }
            };
        }

        return await Task.Run(() =>
        {
            var ptr = MarmotInterop.GenerateKeyPackage(_client, out int length);
            if (ptr == IntPtr.Zero)
            {
                throw new MarmotException(GetLastError() ?? "Failed to generate KeyPackage");
            }

            try
            {
                var data = new byte[length];
                Marshal.Copy(ptr, data, 0, length);

                // Parse the JSON response containing content and tags
                var responseJson = System.Text.Encoding.UTF8.GetString(data);
                _logger.LogDebug("KeyPackage response: {Response}", responseJson[..Math.Min(200, responseJson.Length)]);

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(responseJson);
                }
                catch (JsonException ex)
                {
                    var preview = responseJson[..Math.Min(100, responseJson.Length)];
                    throw new MarmotException(
                        $"Failed to parse KeyPackage JSON from native DLL. Response starts with: \"{preview}\". " +
                        "This usually means the DLL is stale — rebuild with 'cargo build --release' and copy to Desktop project.", ex);
                }
                using var _ = doc;
                var root = doc.RootElement;

                var result = new KeyPackageResult();

                if (root.TryGetProperty("content", out var contentElement))
                {
                    result.Content = contentElement.GetString() ?? "";
                    result.Data = Convert.FromBase64String(result.Content);
                }

                if (root.TryGetProperty("tags", out var tagsElement))
                {
                    foreach (var tagArray in tagsElement.EnumerateArray())
                    {
                        var tag = new List<string>();
                        foreach (var item in tagArray.EnumerateArray())
                        {
                            tag.Add(item.GetString() ?? "");
                        }
                        result.Tags.Add(tag);
                    }
                }

                _logger.LogInformation("Generated KeyPackage with {TagCount} MDK-provided tags", result.Tags.Count);
                return result;
            }
            finally
            {
                MarmotInterop.FreeBuffer(ptr);
            }
        });
    }

    public async Task<(byte[] groupId, ulong epoch)> CreateGroupAsync(string groupName)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var mockGroupId = Guid.NewGuid().ToByteArray();
            _mockGroups[Convert.ToHexString(mockGroupId)] = new MockGroup
            {
                Name = groupName,
                Epoch = 0,
                Members = new List<string> { _publicKeyHex! }
            };
            return (mockGroupId, 0);
        }

        return await Task.Run(() =>
        {
            var ptr = MarmotInterop.CreateGroup(_client, groupName, out int groupIdLength, out ulong epoch);
            if (ptr == IntPtr.Zero)
            {
                throw new MarmotException(GetLastError() ?? "Failed to create group");
            }

            try
            {
                var groupId = new byte[groupIdLength];
                Marshal.Copy(ptr, groupId, 0, groupIdLength);
                return (groupId, epoch);
            }
            finally
            {
                MarmotInterop.FreeBuffer(ptr);
            }
        });
    }

    /// <summary>
    /// Result of adding a member to an MLS group.
    /// </summary>
    public class AddMemberResult
    {
        /// <summary>
        /// Welcome data to send to the new member (for kind 444 event).
        /// </summary>
        public byte[]? WelcomeData { get; set; }

        /// <summary>
        /// Commit/evolution event to publish (for kind 445 event).
        /// </summary>
        public byte[]? CommitData { get; set; }
    }

    public async Task<AddMemberResult> AddMemberAsync(byte[] groupId, byte[] keyPackageData)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var mockWelcome = new byte[512];
            new Random().NextBytes(mockWelcome);
            var mockCommit = new byte[256];
            new Random().NextBytes(mockCommit);
            return new AddMemberResult { WelcomeData = mockWelcome, CommitData = mockCommit };
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);
            var keyPackageHandle = GCHandle.Alloc(keyPackageData, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.AddMember(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    keyPackageHandle.AddrOfPinnedObject(),
                    keyPackageData.Length,
                    out int responseLength);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to add member");
                }

                try
                {
                    var responseData = new byte[responseLength];
                    Marshal.Copy(ptr, responseData, 0, responseLength);

                    // Parse the JSON response containing both welcome and commit
                    var responseJson = System.Text.Encoding.UTF8.GetString(responseData);
                    _logger.LogDebug("AddMember native response ({Len} bytes): {Json}",
                        responseJson.Length, responseJson[..Math.Min(500, responseJson.Length)]);

                    using var doc = JsonDocument.Parse(responseJson);
                    var root = doc.RootElement;

                    var result = new AddMemberResult();

                    if (root.TryGetProperty("welcome", out var welcomeElement) && welcomeElement.ValueKind != JsonValueKind.Null)
                    {
                        // Store the welcome rumor event JSON as bytes — process_welcome will
                        // combine this with the wrapper_event_id later
                        result.WelcomeData = System.Text.Encoding.UTF8.GetBytes(welcomeElement.GetRawText());
                        _logger.LogDebug("AddMember welcome data: {Kind}, {Len} bytes",
                            welcomeElement.ValueKind, result.WelcomeData.Length);
                    }

                    if (root.TryGetProperty("commit", out var commitElement) && commitElement.ValueKind != JsonValueKind.Null)
                    {
                        result.CommitData = System.Text.Encoding.UTF8.GetBytes(commitElement.GetRawText());
                        _logger.LogDebug("AddMember commit data: {Kind}, {Len} bytes",
                            commitElement.ValueKind, result.CommitData.Length);
                    }

                    return result;
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                }
            }
            finally
            {
                groupIdHandle.Free();
                keyPackageHandle.Free();
            }
        });
    }

    public async Task<(byte[] groupId, string groupName, ulong epoch, List<string> members)> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var mockGroupId = new byte[16];
            new Random().NextBytes(mockGroupId);
            return (mockGroupId, "Mock Group", 1, new List<string> { _publicKeyHex! });
        }

        return await Task.Run(() =>
        {
            // The native process_welcome expects JSON: {"wrapper_event_id": "...", "rumor_event": {...}}
            // where rumor_event is a single UnsignedEvent JSON object.
            // welcomeData from add_member's "welcome" field may be an array of rumor events
            // (e.g. [{...}]) — extract the first element if so.
            var rumorJson = System.Text.Encoding.UTF8.GetString(welcomeData);

            try
            {
                using var doc = JsonDocument.Parse(rumorJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    rumorJson = doc.RootElement[0].GetRawText();
                    _logger.LogDebug("ProcessWelcome: extracted first rumor from array of {Count}", doc.RootElement.GetArrayLength());
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "ProcessWelcome: could not parse welcome data as JSON, using raw bytes");
            }

            var inputJson = $"{{\"wrapper_event_id\":\"{wrapperEventId}\",\"rumor_event\":{rumorJson}}}";
            _logger.LogDebug("ProcessWelcome input JSON ({Len} chars): {Json}",
                inputJson.Length, inputJson[..Math.Min(300, inputJson.Length)]);

            var inputBytes = System.Text.Encoding.UTF8.GetBytes(inputJson);
            var welcomeHandle = GCHandle.Alloc(inputBytes, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.ProcessWelcome(
                    _client,
                    welcomeHandle.AddrOfPinnedObject(),
                    inputBytes.Length,
                    out int groupIdLength,
                    out ulong epoch,
                    out IntPtr groupNamePtr,
                    out IntPtr membersJsonPtr);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to process welcome");
                }

                try
                {
                    var groupId = new byte[groupIdLength];
                    Marshal.Copy(ptr, groupId, 0, groupIdLength);

                    var groupName = Marshal.PtrToStringUTF8(groupNamePtr) ?? "";
                    var membersJson = Marshal.PtrToStringUTF8(membersJsonPtr) ?? "[]";
                    var members = JsonSerializer.Deserialize<List<string>>(membersJson) ?? new List<string>();

                    return (groupId, groupName, epoch, members);
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                    MarmotInterop.FreeString(groupNamePtr);
                    MarmotInterop.FreeString(membersJsonPtr);
                }
            }
            finally
            {
                welcomeHandle.Free();
            }
        });
    }

    public async Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation - just encode the plaintext
            var mockCiphertext = System.Text.Encoding.UTF8.GetBytes(plaintext);
            return mockCiphertext;
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.EncryptMessage(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    plaintext,
                    out int ciphertextLength);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to encrypt message");
                }

                try
                {
                    var ciphertext = new byte[ciphertextLength];
                    Marshal.Copy(ptr, ciphertext, 0, ciphertextLength);
                    return ciphertext;
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                }
            }
            finally
            {
                groupIdHandle.Free();
            }
        });
    }

    public async Task<(string senderPublicKey, string plaintext, ulong epoch)> DecryptMessageAsync(byte[] groupId, byte[] ciphertext)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation - just decode the ciphertext
            var plaintext = System.Text.Encoding.UTF8.GetString(ciphertext);
            return (_publicKeyHex!, plaintext, 0);
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);
            var ciphertextHandle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.DecryptMessage(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    ciphertextHandle.AddrOfPinnedObject(),
                    ciphertext.Length,
                    out IntPtr senderPublicKeyPtr,
                    out ulong epoch);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to decrypt message");
                }

                try
                {
                    var plaintext = Marshal.PtrToStringUTF8(ptr) ?? "";
                    var senderPublicKey = Marshal.PtrToStringUTF8(senderPublicKeyPtr) ?? "";

                    return (senderPublicKey, plaintext, epoch);
                }
                finally
                {
                    MarmotInterop.FreeString(ptr);
                    MarmotInterop.FreeString(senderPublicKeyPtr);
                }
            }
            finally
            {
                groupIdHandle.Free();
                ciphertextHandle.Free();
            }
        });
    }

    public async Task ProcessCommitAsync(byte[] groupId, byte[] commitData)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation - no-op
            return;
        }

        await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);
            var commitHandle = GCHandle.Alloc(commitData, GCHandleType.Pinned);

            try
            {
                var result = MarmotInterop.ProcessCommit(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    commitHandle.AddrOfPinnedObject(),
                    commitData.Length);

                if (result != 0)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to process commit");
                }
            }
            finally
            {
                groupIdHandle.Free();
                commitHandle.Free();
            }
        });
    }

    public async Task<byte[]> UpdateKeysAsync(byte[] groupId)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation - increment epoch
            var key = Convert.ToHexString(groupId);
            if (_mockGroups.TryGetValue(key, out var group))
            {
                group.Epoch++;
            }
            var mockCommit = new byte[128];
            new Random().NextBytes(mockCommit);
            return mockCommit;
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.UpdateKeys(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    out int commitLength);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to update keys");
                }

                try
                {
                    var commitData = new byte[commitLength];
                    Marshal.Copy(ptr, commitData, 0, commitLength);
                    return commitData;
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                }
            }
            finally
            {
                groupIdHandle.Free();
            }
        });
    }

    public async Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var mockCommit = new byte[128];
            new Random().NextBytes(mockCommit);
            return mockCommit;
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.RemoveMember(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    memberPublicKey,
                    out int commitLength);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to remove member");
                }

                try
                {
                    var commitData = new byte[commitLength];
                    Marshal.Copy(ptr, commitData, 0, commitLength);
                    return commitData;
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                }
            }
            finally
            {
                groupIdHandle.Free();
            }
        });
    }

    public async Task<(byte[] groupId, string groupName, ulong epoch, List<string> members)?> GetGroupInfoAsync(byte[] groupId)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var key = Convert.ToHexString(groupId);
            if (_mockGroups.TryGetValue(key, out var group))
            {
                return (groupId, group.Name, group.Epoch, group.Members);
            }
            return null;
        }

        return await Task.Run<(byte[] groupId, string groupName, ulong epoch, List<string> members)?>(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);

            try
            {
                var result = MarmotInterop.GetGroupInfo(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    out IntPtr groupNamePtr,
                    out ulong epoch,
                    out IntPtr membersJsonPtr);

                if (result != 0)
                {
                    return null;
                }

                try
                {
                    var groupName = Marshal.PtrToStringUTF8(groupNamePtr) ?? "";
                    var membersJson = Marshal.PtrToStringUTF8(membersJsonPtr) ?? "[]";
                    var members = JsonSerializer.Deserialize<List<string>>(membersJson) ?? new List<string>();

                    return (groupId, groupName, epoch, members);
                }
                finally
                {
                    MarmotInterop.FreeString(groupNamePtr);
                    MarmotInterop.FreeString(membersJsonPtr);
                }
            }
            finally
            {
                groupIdHandle.Free();
            }
        });
    }

    public async Task<byte[]> ExportGroupStateAsync(byte[] groupId)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var key = Convert.ToHexString(groupId);
            if (_mockGroups.TryGetValue(key, out var group))
            {
                return JsonSerializer.SerializeToUtf8Bytes(group);
            }
            return Array.Empty<byte>();
        }

        return await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);

            try
            {
                var ptr = MarmotInterop.ExportGroupState(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    out int stateLength);

                if (ptr == IntPtr.Zero)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to export group state");
                }

                try
                {
                    var state = new byte[stateLength];
                    Marshal.Copy(ptr, state, 0, stateLength);
                    return state;
                }
                finally
                {
                    MarmotInterop.FreeBuffer(ptr);
                }
            }
            finally
            {
                groupIdHandle.Free();
            }
        });
    }

    public async Task ImportGroupStateAsync(byte[] groupId, byte[] state)
    {
        EnsureInitialized();

        if (_client == IntPtr.Zero)
        {
            // Mock implementation
            var key = Convert.ToHexString(groupId);
            var group = JsonSerializer.Deserialize<MockGroup>(state);
            if (group != null)
            {
                _mockGroups[key] = group;
            }
            return;
        }

        await Task.Run(() =>
        {
            var groupIdHandle = GCHandle.Alloc(groupId, GCHandleType.Pinned);
            var stateHandle = GCHandle.Alloc(state, GCHandleType.Pinned);

            try
            {
                var result = MarmotInterop.ImportGroupState(
                    _client,
                    groupIdHandle.AddrOfPinnedObject(),
                    groupId.Length,
                    stateHandle.AddrOfPinnedObject(),
                    state.Length);

                if (result != 0)
                {
                    throw new MarmotException(GetLastError() ?? "Failed to import group state");
                }
            }
            finally
            {
                groupIdHandle.Free();
                stateHandle.Free();
            }
        });
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("MarmotWrapper not initialized. Call InitializeAsync first.");
        }
    }

    private static string? GetLastError()
    {
        try
        {
            var errorPtr = MarmotInterop.GetLastError();
            if (errorPtr == IntPtr.Zero) return null;

            var error = Marshal.PtrToStringUTF8(errorPtr);
            MarmotInterop.FreeString(errorPtr);
            return error;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_client != IntPtr.Zero)
        {
            try
            {
                MarmotInterop.DestroyClient(_client);
            }
            catch
            {
                // Ignore errors during cleanup
            }
            _client = IntPtr.Zero;
        }

        _disposed = true;
    }

    private class MockGroup
    {
        public string Name { get; set; } = "";
        public ulong Epoch { get; set; }
        public List<string> Members { get; set; } = new();
    }
}

public class MarmotException : Exception
{
    public MarmotException(string message) : base(message) { }
    public MarmotException(string message, Exception innerException) : base(message, innerException) { }
}
