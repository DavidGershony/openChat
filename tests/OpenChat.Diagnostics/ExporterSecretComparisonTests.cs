using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenChat.Core.Configuration;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Diagnostics.TestHelpers;
using MarmotCs.Protocol.Crypto;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Diagnostics;

/// <summary>
/// Compares MLS exporter secrets between managed (dotnet-mls) and rust (OpenMLS via MDK)
/// backends to find where the key schedule diverges.
/// </summary>
[Trait("Category", "ExporterSecret")]
public class ExporterSecretComparisonTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _dbPaths = new();

    public ExporterSecretComparisonTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in _dbPaths) try { if (File.Exists(p)) File.Delete(p); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(User user, StorageService storage, IMlsService mls)> CreateUser(string name, string backend)
    {
        var ns = new NostrService();
        var keys = ns.GenerateKeyPair();
        var dbPath = Path.Combine(Path.GetTempPath(), $"oc_expcomp_{name}_{Guid.NewGuid()}.db");
        _dbPaths.Add(dbPath);
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            PublicKeyHex = keys.publicKeyHex,
            PrivateKeyHex = keys.privateKeyHex,
            Npub = keys.npub, Nsec = keys.nsec,
            DisplayName = name, IsCurrentUser = true,
            CreatedAt = DateTime.UtcNow
        };
        await storage.SaveCurrentUserAsync(user);
        IMlsService mls = backend switch
        {
            "managed" => new ManagedMlsService(storage),
            "rust" => new MlsService(storage),
            _ => throw new ArgumentException(backend)
        };
        await mls.InitializeAsync(keys.privateKeyHex, keys.publicKeyHex);
        return (user, storage, mls);
    }

    private static KeyPackage WrapKp(KeyPackage kp, string pubkey)
    {
        var content = Convert.ToBase64String(kp.Data);
        var tags = JsonSerializer.Serialize(kp.NostrTags ?? new List<List<string>>());
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var sig = Convert.ToHexString(RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();
        kp.EventJson = $$"""{"id":"{{id}}","pubkey":"{{pubkey}}","created_at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"kind":443,"tags":{{tags}},"content":"{{content}}","sig":"{{sig}}"}""";
        kp.NostrEventId = id;
        return kp;
    }

    [SkippableFact]
    public async Task CompareExporterSecrets_ManagedVsRust_SameGroup()
    {
        Skip.IfNot(File.Exists(Path.Combine(AppContext.BaseDirectory, "openchat_native.dll")),
            "Rust native DLL not found");

        _output.WriteLine("═══════════════════════════════════════════════════════════");
        _output.WriteLine("  Exporter Secret Comparison: managed vs rust");
        _output.WriteLine("═══════════════════════════════════════════════════════════");

        // Alice (managed) creates group and adds Bob (managed) + Charlie (rust)
        var (_, _, aliceMls) = await CreateUser("Alice", "managed");
        var (_, _, bobMls) = await CreateUser("Bob-managed", "managed");
        var (_, _, charlieMls) = await CreateUser("Charlie-rust", "rust");

        // Alice creates group
        var group = await aliceMls.CreateGroupAsync("Exporter Test", new[] { "wss://relay.test" });
        _output.WriteLine($"Group: {Convert.ToHexString(group.GroupId).ToLowerInvariant()[..16]}..., epoch={group.Epoch}");

        // Generate KeyPackages
        var kpBob = WrapKp(await bobMls.GenerateKeyPackageAsync(), "bob");
        var kpCharlie = WrapKp(await charlieMls.GenerateKeyPackageAsync(), "charlie");

        // Alice adds Bob (managed)
        var welcomeBob = await aliceMls.AddMemberAsync(group.GroupId, kpBob);
        _output.WriteLine($"Added Bob: welcome={welcomeBob.WelcomeData.Length} bytes");

        // Bob processes Welcome
        var bobGroup = await bobMls.ProcessWelcomeAsync(welcomeBob.WelcomeData,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _output.WriteLine($"Bob joined: epoch={bobGroup.Epoch}");

        // Alice adds Charlie (rust)
        var welcomeCharlie = await aliceMls.AddMemberAsync(group.GroupId, kpCharlie);
        _output.WriteLine($"Added Charlie: welcome={welcomeCharlie.WelcomeData.Length} bytes");

        // Bob processes commit
        if (welcomeCharlie.CommitData is { Length: > 0 })
        {
            try
            {
                var r = await bobMls.DecryptMessageAsync(bobGroup.GroupId, welcomeCharlie.CommitData);
                _output.WriteLine($"Bob processed commit: IsCommit={r.IsCommit}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Bob commit failed: {ex.Message}");
            }
        }

        // Charlie processes Welcome
        try
        {
            var charlieGroup = await charlieMls.ProcessWelcomeAsync(welcomeCharlie.WelcomeData,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            _output.WriteLine($"Charlie (rust) joined: epoch={charlieGroup.Epoch}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Charlie (rust) Welcome FAILED: {ex.Message}");
            _output.WriteLine("*** If this fails, the Welcome format is incompatible between managed and rust ***");
            return;
        }

        // Now compare exporter secrets
        _output.WriteLine("\n--- Exporter Secret Comparison ---");

        var aliceExporter = aliceMls.GetMediaExporterSecret(group.GroupId);
        // GetMediaExporterSecret uses MLS-Exporter("marmot", "encrypted-media", 32)
        // But we need MLS-Exporter("marmot", "group-event", 32) - let me use GetExporterSecret
        // Actually GetExporterSecret is internal to Mdk. Let me use the encryption path instead.

        // Encrypt a message with Alice → see if Charlie (rust) can MIP-03 decrypt it
        _output.WriteLine("\nAlice encrypts at epoch 2...");
        var aliceMsg = await aliceMls.EncryptMessageAsync(group.GroupId, "Test from Alice");
        _output.WriteLine($"Alice encrypted: {aliceMsg.Length} bytes");

        // Extract the base64 content (MIP-03 encrypted)
        using var doc = JsonDocument.Parse(aliceMsg);
        var b64Content = doc.RootElement.GetProperty("content").GetString()!;
        var encryptedBytes = Convert.FromBase64String(b64Content);
        _output.WriteLine($"MIP-03 ciphertext: {encryptedBytes.Length} bytes, nonce={Convert.ToHexString(encryptedBytes[..12])}");

        // Try to decrypt with Charlie (rust)
        _output.WriteLine("\nCharlie (rust) tries to decrypt Alice's message...");
        try
        {
            var result = await charlieMls.DecryptMessageAsync(
                (await charlieMls.GetGroupInfoAsync(group.GroupId))!.GroupId,
                encryptedBytes);
            _output.WriteLine($"Charlie DECRYPTED: \"{result.Plaintext}\"");
            _output.WriteLine("*** EXPORTER SECRETS MATCH — managed and rust are compatible! ***");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Charlie DECRYPT FAILED: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine("*** EXPORTER SECRETS DIVERGE — key schedule mismatch between dotnet-mls and OpenMLS ***");
        }

        // Also test reverse: Charlie encrypts, Alice decrypts
        _output.WriteLine("\nCharlie (rust) encrypts...");
        try
        {
            var charlieMsg = await charlieMls.EncryptMessageAsync(group.GroupId, "Test from Charlie rust");
            _output.WriteLine($"Charlie encrypted: {charlieMsg.Length} bytes");

            // Alice tries to decrypt
            using var cDoc = JsonDocument.Parse(charlieMsg);
            var cContent = cDoc.RootElement.GetProperty("content").GetString()!;
            var cBytes = Convert.FromBase64String(cContent);

            var aResult = await aliceMls.DecryptMessageAsync(group.GroupId, cBytes);
            _output.WriteLine($"Alice DECRYPTED Charlie: \"{aResult.Plaintext}\"");
            _output.WriteLine("*** BIDIRECTIONAL — both directions work! ***");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Reverse decrypt FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        _output.WriteLine("\n═══════════════════════════════════════════════════════════");
    }
}
