using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Configuration;
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

    private string? _initializationError;

    /// <summary>
    /// Whether the native MLS client is loaded and ready.
    /// </summary>
    public bool IsUsingNativeClient => _client != IntPtr.Zero;

    /// <summary>
    /// Maximum buffer size accepted from native code (100 MB).
    /// Prevents allocation of absurdly large arrays from a buggy or compromised native DLL.
    /// </summary>
    private const int MaxNativeBufferSize = 100_000_000;

    /// <summary>
    /// Validates a buffer length returned by the native DLL before using it for allocation.
    /// </summary>
    internal static void ValidateNativeBufferLength(int length, string operation)
    {
        if (length < 0)
            throw new MarmotException($"Native DLL returned negative buffer length ({length}) for {operation}");
        if (length > MaxNativeBufferSize)
            throw new MarmotException($"Native DLL returned oversized buffer length ({length}) for {operation}. Max: {MaxNativeBufferSize}");
    }

    public MarmotWrapper()
    {
        _logger = LoggingConfiguration.CreateLogger<MarmotWrapper>();
        _logger.LogDebug("MarmotWrapper instance created");
    }

    public async Task InitializeAsync(string privateKeyHex, string publicKeyHex)
    {
        _logger.LogInformation("Initializing MarmotWrapper");
        _logger.LogDebug("Public key: {PubKey}...", publicKeyHex[..16]);

        try
        {
            // Use persistent SQLite storage in the profile's data directory
            var dbPath = Path.Combine(ProfileConfiguration.DataDirectory, "mls_native.db");
            _logger.LogInformation("Using native MLS SQLite storage at: {DbPath}", dbPath);

            _logger.LogDebug("Attempting to load native Marmot library");
            _client = MarmotInterop.CreateClient(privateKeyHex, publicKeyHex, dbPath);
            if (_client == IntPtr.Zero)
            {
                var error = GetLastError() ?? "Failed to create Marmot client";
                _logger.LogError("Failed to create native Marmot client: {Error}", error);
                throw new MarmotException(error);
            }
            _logger.LogInformation("Successfully initialized native Marmot client with persistent storage");
            _initialized = true;
        }
        catch (DllNotFoundException ex)
        {
            _initializationError = "Native MLS library not available on this platform. Group encryption is disabled.";
            _logger.LogError(ex, "{Error}", _initializationError);
            throw new MarmotException(_initializationError, ex);
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

        return await Task.Run(() =>
        {
            var ptr = MarmotInterop.GenerateKeyPackage(_client, out int length);
            if (ptr == IntPtr.Zero)
            {
                throw new MarmotException(GetLastError() ?? "Failed to generate KeyPackage");
            }

            try
            {
                ValidateNativeBufferLength(length, "GenerateKeyPackage");
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

        return await Task.Run(() =>
        {
            var ptr = MarmotInterop.CreateGroup(_client, groupName, out int groupIdLength, out ulong epoch);
            if (ptr == IntPtr.Zero)
            {
                throw new MarmotException(GetLastError() ?? "Failed to create group");
            }

            try
            {
                ValidateNativeBufferLength(groupIdLength, "CreateGroup");
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
                    ValidateNativeBufferLength(responseLength, "AddMember");
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

        return await Task.Run(() =>
        {
            // The native process_welcome expects JSON: {"wrapper_event_id": "...", "rumor_event": {...}}
            // where rumor_event is a single UnsignedEvent JSON object.
            //
            // Welcome data comes in two formats:
            // 1. Rust MDK: JSON rumor event (or array) where content is base64-encoded MLS Welcome
            // 2. C# MDK: raw MLS Welcome TLS bytes (binary, starts with 0x00-0x0F)
            //
            // Detect format and normalize to what the native DLL expects.
            string rumorJson;
            byte firstByte = welcomeData[0];

            if (firstByte != (byte)'{' && firstByte != (byte)'[')
            {
                // Raw MLS TLS bytes from C# MDK — wrap in a synthetic rumor event
                var base64Content = Convert.ToBase64String(welcomeData);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                rumorJson = $"{{\"id\":\"{wrapperEventId}\",\"pubkey\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"created_at\":{now},\"kind\":444,\"tags\":[],\"content\":\"{base64Content}\"}}";
                _logger.LogInformation("ProcessWelcome: wrapped {Len} bytes of raw MLS TLS data in synthetic rumor event", welcomeData.Length);
            }
            else
            {
                // JSON from Rust MDK — may be an array of rumor events, extract first
                rumorJson = System.Text.Encoding.UTF8.GetString(welcomeData);
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
                    _logger.LogWarning(ex, "ProcessWelcome: could not parse welcome data as JSON");
                    throw new MarmotException("Welcome data is not valid JSON or MLS TLS data", ex);
                }
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
                    ValidateNativeBufferLength(groupIdLength, "ProcessWelcome");
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
                    ValidateNativeBufferLength(ciphertextLength, "EncryptMessage");
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
                    ValidateNativeBufferLength(commitLength, "UpdateKeys");
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
                    ValidateNativeBufferLength(commitLength, "RemoveMember");
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
                    ValidateNativeBufferLength(stateLength, "ExportGroupState");
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
            var message = _initializationError
                ?? "MLS service not initialized. Call InitializeAsync first.";
            throw new MarmotException(message);
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
}

public class MarmotException : Exception
{
    public MarmotException(string message) : base(message) { }
    public MarmotException(string message, Exception innerException) : base(message, innerException) { }
}
