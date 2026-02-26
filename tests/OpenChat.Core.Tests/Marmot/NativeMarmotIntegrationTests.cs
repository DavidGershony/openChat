using System.Text.Json;
using OpenChat.Core.Marmot;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests.Marmot;

/// <summary>
/// Integration tests that exercise the real native openchat_native.dll.
/// These are skipped when the DLL is not present in the test output directory.
/// Run with: dotnet test --filter "Category=Native"
/// </summary>
[Trait("Category", "Native")]
public class NativeMarmotIntegrationTests : IAsyncLifetime
{
    private MarmotWrapper _clientA = null!;
    private MarmotWrapper _clientB = null!;
    private string _pubKeyA = null!;
    private string _privKeyA = null!;
    private string _pubKeyB = null!;
    private string _privKeyB = null!;

    private static bool NativeDllAvailable()
    {
        // Check if the DLL exists alongside the test assembly
        var dllPath = Path.Combine(AppContext.BaseDirectory, "openchat_native.dll");
        return File.Exists(dllPath);
    }

    public async Task InitializeAsync()
    {
        if (!NativeDllAvailable())
            return; // Tests will be skipped via Skip

        var nostrService = new NostrService();
        (_privKeyA, _pubKeyA, _, _) = nostrService.GenerateKeyPair();
        (_privKeyB, _pubKeyB, _, _) = nostrService.GenerateKeyPair();

        _clientA = new MarmotWrapper();
        await _clientA.InitializeAsync(_privKeyA, _pubKeyA);

        _clientB = new MarmotWrapper();
        await _clientB.InitializeAsync(_privKeyB, _pubKeyB);
    }

    public Task DisposeAsync()
    {
        _clientA?.Dispose();
        _clientB?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task GenerateKeyPackage_NativeDll_ReturnsValidJsonWithContentAndTags()
    {
        // Catches Bug #1 (stale DLL returning wrong format).
        // A stale DLL might return raw bytes instead of JSON, or JSON with different field names.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient, "Expected native client, got mock fallback");

        var result = await _clientA.GenerateKeyPackageAsync();

        // Content must be valid base64
        Assert.False(string.IsNullOrEmpty(result.Content), "Content should not be empty");
        var decoded = Convert.FromBase64String(result.Content);
        Assert.True(decoded.Length >= 64,
            $"KeyPackage data is only {decoded.Length} bytes — expected >= 64 for a real MLS KeyPackage");

        // Data should match content
        Assert.Equal(decoded, result.Data);

        // Must have MIP-00 required tags
        Assert.Contains(result.Tags, t => t.Count >= 2 && t[0] == "encoding");
        Assert.Contains(result.Tags, t => t.Count >= 2 && t[0] == "mls_protocol_version");
        Assert.Contains(result.Tags, t => t.Count >= 2 && t[0] == "mls_ciphersuite");
    }

    [SkippableFact]
    public async Task CreateGroup_NativeDll_ReturnsNonEmptyGroupId()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient);

        var (groupId, epoch) = await _clientA.CreateGroupAsync("Native Test Group");

        Assert.NotNull(groupId);
        Assert.True(groupId.Length > 0, "Group ID should be non-empty");
    }

    [SkippableFact]
    public async Task AddMember_SameVersion_KeyPackageRoundtrip()
    {
        // Catches Bug #2 (cross-instance MLS incompatibility).
        // Client A creates a group, Client B generates a KeyPackage,
        // Client A adds Client B using B's KeyPackage wrapped in event JSON.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient);
        Assert.True(_clientB.IsUsingNativeClient);

        // A creates group
        var (groupId, _) = await _clientA.CreateGroupAsync("Cross-Client Test");

        // B generates KeyPackage
        var kpResult = await _clientB.GenerateKeyPackageAsync();

        // Wrap in Nostr event JSON (the native add_member expects this)
        var eventJson = BuildKeyPackageEventJson(_pubKeyB, kpResult.Content, kpResult.Tags);
        var eventJsonBytes = System.Text.Encoding.UTF8.GetBytes(eventJson);

        // A adds B
        var addResult = await _clientA.AddMemberAsync(groupId, eventJsonBytes);

        Assert.NotNull(addResult.WelcomeData);
        Assert.True(addResult.WelcomeData!.Length > 0, "WelcomeData should be non-empty");
    }

    [SkippableFact]
    public async Task AddMember_WithRandomBytes_ThrowsMarmotException()
    {
        // Verify that incompatible/garbage data is rejected loudly, not silently ignored.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient);

        var (groupId, _) = await _clientA.CreateGroupAsync("Rejection Test");
        var randomBytes = new byte[256];
        new Random(42).NextBytes(randomBytes);

        await Assert.ThrowsAsync<MarmotException>(() =>
            _clientA.AddMemberAsync(groupId, randomBytes));
    }

    [SkippableFact]
    public async Task FullMlsLifecycle_TwoClients_GroupCreateAddMemberProcessWelcome()
    {
        // Full lifecycle: A creates group, B generates KP, A adds B, B processes Welcome.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient);
        Assert.True(_clientB.IsUsingNativeClient);

        // A creates group
        var (groupIdA, _) = await _clientA.CreateGroupAsync("Lifecycle Test");

        // B generates KeyPackage
        var kpResult = await _clientB.GenerateKeyPackageAsync();

        // A adds B
        var eventJson = BuildKeyPackageEventJson(_pubKeyB, kpResult.Content, kpResult.Tags);
        var eventJsonBytes = System.Text.Encoding.UTF8.GetBytes(eventJson);
        var addResult = await _clientA.AddMemberAsync(groupIdA, eventJsonBytes);

        Assert.NotNull(addResult.WelcomeData);

        // B processes Welcome
        // process_welcome needs the kind-444 wrapper event ID + the welcome rumor data
        var fakeWrapperEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var (groupIdB, groupName, epoch, members) = await _clientB.ProcessWelcomeAsync(addResult.WelcomeData!, fakeWrapperEventId);

        Assert.True(groupIdB.Length > 0, "B should get a valid group ID from Welcome");
        // Both should be in the same group (group IDs should match)
        Assert.Equal(groupIdA, groupIdB);
    }

    [SkippableFact]
    public async Task FullMlsLifecycle_TwoClients_EncryptDecryptRoundtrip()
    {
        // After group setup, A encrypts a message, B decrypts it, and vice versa.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Assert.True(_clientA.IsUsingNativeClient);
        Assert.True(_clientB.IsUsingNativeClient);

        // Setup: A creates group, adds B, B processes Welcome
        var (groupIdA, _) = await _clientA.CreateGroupAsync("Encrypt Test");
        var kpResult = await _clientB.GenerateKeyPackageAsync();
        var eventJson = BuildKeyPackageEventJson(_pubKeyB, kpResult.Content, kpResult.Tags);
        var eventJsonBytes = System.Text.Encoding.UTF8.GetBytes(eventJson);
        var addResult = await _clientA.AddMemberAsync(groupIdA, eventJsonBytes);
        var fakeWrapperEventId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var (groupIdB, _, _, _) = await _clientB.ProcessWelcomeAsync(addResult.WelcomeData!, fakeWrapperEventId);

        // B must process the commit from the add-member operation
        if (addResult.CommitData != null && addResult.CommitData.Length > 0)
        {
            await _clientA.ProcessCommitAsync(groupIdA, addResult.CommitData);
        }

        // A encrypts, B decrypts
        var ciphertextA = await _clientA.EncryptMessageAsync(groupIdA, "Hello from A!");
        var (senderA, plaintextA, _) = await _clientB.DecryptMessageAsync(groupIdB, ciphertextA);
        Assert.Equal("Hello from A!", plaintextA);

        // B encrypts, A decrypts
        var ciphertextB = await _clientB.EncryptMessageAsync(groupIdB, "Hello from B!");
        var (senderB, plaintextB, _) = await _clientA.DecryptMessageAsync(groupIdA, ciphertextB);
        Assert.Equal("Hello from B!", plaintextB);
    }

    [SkippableFact]
    public async Task NativeClient_InvalidKeys_DocumentsBehavior()
    {
        // Documents the behavior when invalid (non-secp256k1) keys are provided.
        // The native DLL may accept bad keys at CreateClient but fail later on operations.
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        var badWrapper = new MarmotWrapper();
        var dummyKey = new string('a', 64);

        try
        {
            await badWrapper.InitializeAsync(dummyKey, dummyKey);

            // If the native DLL accepted the keys, operations should still fail
            // because the keys aren't valid secp256k1 points
            if (badWrapper.IsUsingNativeClient)
            {
                // Try an operation — it may fail or produce garbage
                var kp = await badWrapper.GenerateKeyPackageAsync();
                Assert.NotNull(kp); // Documents that bad keys can produce output
            }
        }
        catch (MarmotException)
        {
            // Also acceptable: native lib rejects the bad keys at some point
        }
        finally
        {
            badWrapper.Dispose();
        }
    }

    /// <summary>
    /// Builds a minimal kind-443 Nostr event JSON for AddMember tests.
    /// Same pattern used in EndToEndChatIntegrationTests.CreateFakeKeyPackageEventJson.
    /// </summary>
    private static string BuildKeyPackageEventJson(string publicKeyHex, string base64Content, List<List<string>> tags)
    {
        var tagsArray = tags.Select(t => t.ToArray()).ToArray();
        var eventObj = new
        {
            id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            pubkey = publicKeyHex,
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            kind = 443,
            tags = tagsArray,
            content = base64Content,
            sig = new string('a', 128) // fake 64-byte hex signature
        };
        return JsonSerializer.Serialize(eventObj);
    }
}
