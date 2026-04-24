using System.Text;
using System.Text.Json;
using DotnetMls.Codec;
using DotnetMls.Crypto;
using DotnetMls.Group;
using DotnetMls.Types;
using MarmotCs.Protocol.Mip01;
using OpenChat.Core.Marmot;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using Xunit;
using OpenChat.Core.Tests.TestHelpers;
using Xunit.Abstractions;
using Extension = DotnetMls.Types.Extension;
using KeyPackage = OpenChat.Core.Models.KeyPackage;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-01 interop tests verifying that C# and Rust MDK implementations
/// produce compatible 0xF2EE (NostrGroupData) extensions.
///
/// Tests exercise the native DLL directly (no relay needed).
/// Requires: openchat_native.dll in test output directory.
/// </summary>
[Trait("Category", "Native")]
public class Mip01InteropTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // Rust MDK client
    private MarmotWrapper _rustClient = null!;
    private string _rustPrivKey = null!;
    private string _rustPubKey = null!;

    // C# MDK clients (need two: one to create group, one to join)
    private ManagedMlsService _managedMlsA = null!;
    private ManagedMlsService _managedMlsB = null!;
    private StorageService _storageA = null!;
    private StorageService _storageB = null!;
    private string _managedPrivKeyA = null!;
    private string _managedPubKeyA = null!;
    private string _managedPrivKeyB = null!;
    private string _managedPubKeyB = null!;
    private string _dbPathA = null!;
    private string _dbPathB = null!;

    public Mip01InteropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool NativeDllAvailable()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "openchat_native.dll");
        return File.Exists(dllPath);
    }

    public async Task InitializeAsync()
    {
        if (!NativeDllAvailable())
            return;

        var nostrService = new NostrService();
        (_rustPrivKey, _rustPubKey, _, _) = nostrService.GenerateKeyPair();
        (_managedPrivKeyA, _managedPubKeyA, _, _) = nostrService.GenerateKeyPair();
        (_managedPrivKeyB, _managedPubKeyB, _, _) = nostrService.GenerateKeyPair();

        // Initialize Rust MDK
        _rustClient = new MarmotWrapper();
        await _rustClient.InitializeAsync(_rustPrivKey, _rustPubKey);

        // Initialize C# MDK A (group creator)
        _dbPathA = Path.Combine(Path.GetTempPath(), $"mip01_interop_A_{Guid.NewGuid()}.db");
        _storageA = new StorageService(_dbPathA, new MockSecureStorage());
        await _storageA.InitializeAsync();
        _managedMlsA = new ManagedMlsService(_storageA);
        await _managedMlsA.InitializeAsync(_managedPrivKeyA, _managedPubKeyA);

        // Initialize C# MDK B (joiner)
        _dbPathB = Path.Combine(Path.GetTempPath(), $"mip01_interop_B_{Guid.NewGuid()}.db");
        _storageB = new StorageService(_dbPathB, new MockSecureStorage());
        await _storageB.InitializeAsync();
        _managedMlsB = new ManagedMlsService(_storageB);
        await _managedMlsB.InitializeAsync(_managedPrivKeyB, _managedPubKeyB);
    }

    public async Task DisposeAsync()
    {
        _rustClient?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        TryDeleteFile(_dbPathA);
        TryDeleteFile(_dbPathB);
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: Rust creates group with 0xF2EE → C# processes Welcome
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a Rust-created group with NostrGroupData (0xF2EE) extension
    /// produces a Welcome that the C# MDK can process successfully, extracting
    /// the correct group name and metadata.
    /// </summary>
    [SkippableFact]
    public async Task RustGroup_CSharpProcessesWelcome_ExtractsGroupMetadata()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        var cs = new CipherSuite0x0001();
        var (sigPriv, sigPub) = cs.GenerateSignatureKeyPair();
        var identity = Convert.FromHexString(_managedPubKeyB);

        // Generate KeyPackage directly using dotnet-mls so we have full key material
        var mlsKp = MlsGroup.CreateKeyPackage(
            cs, identity, sigPriv, sigPub,
            out var initPrivateKey, out var hpkePrivateKey,
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE });

        var kpBytes = TlsCodec.Serialize(w => mlsKp.WriteTo(w));
        _output.WriteLine($"C# KeyPackage: {kpBytes.Length} bytes");

        // Build the MIP-00 event content + tags
        var (content, tagsArr) = MarmotCs.Protocol.Mip00.KeyPackageEventBuilder.BuildKeyPackageEvent(
            kpBytes, _managedPubKeyB, new[] { "wss://relay.example.com" },
            supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE });

        // Convert string[][] to List<List<string>> for the event builder
        var tags = tagsArr.Select(t => t.ToList()).ToList();

        var eventJson = Mip00InteropTests_BuildSignedKeyPackageEvent(
            kpBytes, tags, _managedPrivKeyB, _managedPubKeyB);

        // Rust creates group
        var (groupId, _) = await _rustClient.CreateGroupAsync("Rust MIP-01 Group");
        _output.WriteLine($"Rust group: {Convert.ToHexString(groupId).ToLowerInvariant()[..16]}...");

        // Rust adds C# member
        var eventJsonBytes = Encoding.UTF8.GetBytes(eventJson);
        var addResult = await _rustClient.AddMemberAsync(groupId, eventJsonBytes);
        Assert.NotNull(addResult.WelcomeData);
        _output.WriteLine($"Rust Welcome: {addResult.WelcomeData.Length} bytes");

        // Extract raw MLS Welcome bytes from the JSON rumor event
        var welcomeJson = Encoding.UTF8.GetString(addResult.WelcomeData);
        using var welcomeDoc = JsonDocument.Parse(welcomeJson);
        var welcomeRoot = welcomeDoc.RootElement;
        var rumorEl = welcomeRoot.ValueKind == JsonValueKind.Array ? welcomeRoot[0] : welcomeRoot;
        var contentStr = rumorEl.GetProperty("content").GetString()!;
        var mlsMessageBytes = Convert.FromBase64String(contentStr);
        _output.WriteLine($"MLSMessage bytes: {mlsMessageBytes.Length}, header: {Convert.ToHexString(mlsMessageBytes[..4])}");

        // Strip MLSMessage header (version=0x0001, wire_format=0x0003)
        var rawWelcomeBytes = mlsMessageBytes[4..];

        // Process Welcome directly using dotnet-mls for full inspection
        var welcomeReader = new TlsReader(rawWelcomeBytes);
        var welcome = Welcome.ReadFrom(welcomeReader);

        var mlsGroup = MlsGroup.ProcessWelcome(
            cs, welcome, mlsKp,
            initPrivateKey, hpkePrivateKey, sigPriv);

        _output.WriteLine($"C# group: {Convert.ToHexString(mlsGroup.GroupId).ToLowerInvariant()[..16]}..., epoch={mlsGroup.Epoch}");

        // Inspect GroupContext.Extensions — should contain 0xF2EE
        var extensions = mlsGroup.GroupContext.Extensions;
        _output.WriteLine($"GroupContext.Extensions: {extensions.Length} extension(s)");

        var f2eeExt = extensions.FirstOrDefault(e => e.ExtensionType == 0xF2EE);
        Assert.NotNull(f2eeExt);

        var ngd = NostrGroupDataExtension.FromExtension(f2eeExt);
        _output.WriteLine($"NostrGroupData: name=\"{ngd.Name}\", version={ngd.Version}, " +
                          $"groupId={Convert.ToHexString(ngd.NostrGroupId).ToLowerInvariant()[..16]}...");

        Assert.Equal("Rust MIP-01 Group", ngd.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: C# creates group with 0xF2EE → Rust processes Welcome
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a C#-created group with NostrGroupData (0xF2EE) extension
    /// produces a Welcome that the Rust MDK can process successfully.
    /// Known issue: dotnet-mls Welcome byte format is not yet compatible with Rust MDK.
    /// </summary>
    [SkippableFact]
    public async Task CSharpGroup_RustProcessesWelcome()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");
        Skip.If(true, "Known dotnet-mls issue: C# Welcome bytes not yet compatible with Rust MDK (key schedule/ratchet tree)");

        // Rust user generates KeyPackage
        var rustKp = await _rustClient.GenerateKeyPackageAsync();
        _output.WriteLine($"Rust KeyPackage: {rustKp.Data.Length} bytes, {rustKp.Tags.Count} tags");

        // C# creates group
        var groupInfo = await _managedMlsA.CreateGroupAsync("CSharp MIP-01 Group", new[] { "wss://relay.test" });
        _output.WriteLine($"C# group: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()[..16]}...");

        // C# adds Rust member — need to build a KeyPackage model with EventJson
        var rustEventJson = BuildRustKeyPackageEventJson(rustKp);
        var kpModel = new KeyPackage
        {
            Id = Guid.NewGuid().ToString(),
            OwnerPublicKey = _rustPubKey,
            Data = rustKp.Data,
            EventJson = rustEventJson,
            CiphersuiteId = 0x0001
        };

        var welcome = await _managedMlsA.AddMemberAsync(groupInfo.GroupId, kpModel);
        Assert.NotNull(welcome.WelcomeData);
        Assert.True(welcome.WelcomeData.Length > 0);
        _output.WriteLine($"C# Welcome: {welcome.WelcomeData.Length} bytes");

        // Rust processes the Welcome
        var wrapperEventId = new string('b', 64);
        var (rustGroupId, rustGroupName, rustEpoch, rustMembers) =
            await _rustClient.ProcessWelcomeAsync(welcome.WelcomeData, wrapperEventId);

        Assert.NotNull(rustGroupId);
        Assert.True(rustGroupId.Length > 0);
        _output.WriteLine($"Rust joined group: {Convert.ToHexString(rustGroupId).ToLowerInvariant()[..16]}..., " +
                          $"name=\"{rustGroupName}\", epoch={rustEpoch}");

        // The group name should come from the 0xF2EE extension
        Assert.Equal("CSharp MIP-01 Group", rustGroupName);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: C# codec wire format — verify field positions
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the NostrGroupDataCodec produces correctly ordered fields
    /// with MLS VarInt length prefixes (RFC 9000 Section 16).
    /// </summary>
    [Fact]
    public void NostrGroupDataCodec_WireFormat_VarIntPrefixes()
    {
        var groupId = new byte[32];
        groupId[0] = 0xFF;

        var data = new NostrGroupData
        {
            Version = 2,
            NostrGroupId = groupId,
            Name = "AB",       // 2 UTF-8 bytes
            Description = "C", // 1 UTF-8 byte
            AdminPubkeys = new byte[32], // 1 admin key
            Relays = new[] { "wss://r" }, // 7 UTF-8 bytes
        };

        byte[] encoded = NostrGroupDataCodec.Encode(data);
        int pos = 0;

        // version: u16 big-endian = 0x0002
        Assert.Equal(0x00, encoded[pos++]);
        Assert.Equal(0x02, encoded[pos++]);

        // nostr_group_id: 32 raw bytes
        Assert.Equal(0xFF, encoded[pos]);
        pos += 32;

        // name: opaque<V> — VarInt length (0x02) + "AB"
        Assert.Equal(0x02, encoded[pos++]); // VarInt 2 (1 byte, top 2 bits=00)
        Assert.Equal((byte)'A', encoded[pos++]);
        Assert.Equal((byte)'B', encoded[pos++]);

        // description: opaque<V> — VarInt length (0x01) + "C"
        Assert.Equal(0x01, encoded[pos++]); // VarInt 1
        Assert.Equal((byte)'C', encoded[pos++]);

        // admin_pubkeys: vector<V> — VarInt total byte count (0x20 = 32)
        Assert.Equal(0x20, encoded[pos++]); // VarInt 32
        pos += 32; // skip the key bytes

        // relays: vector<V> — VarInt total byte count = 1 (inner VarInt) + 7 (data) = 8
        Assert.Equal(0x08, encoded[pos++]); // VarInt 8
        // inner: opaque<V> — VarInt length (0x07) + "wss://r"
        Assert.Equal(0x07, encoded[pos++]); // VarInt 7
        Assert.Equal((byte)'w', encoded[pos]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: Extension type constant
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies the MLS extension type wrapping uses 0xF2EE.
    /// </summary>
    [Fact]
    public void NostrGroupDataExtension_ProducesCorrectExtensionType()
    {
        var data = new NostrGroupData { Name = "Test" };
        var ext = NostrGroupDataExtension.ToExtension(data);

        Assert.Equal(0xF2EE, ext.ExtensionType);
        Assert.NotEmpty(ext.ExtensionData);

        // Round-trip through FromExtension
        var decoded = NostrGroupDataExtension.FromExtension(ext);
        Assert.Equal("Test", decoded.Name);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reuse MIP-00 signed event builder.
    /// </summary>
    private static string Mip00InteropTests_BuildSignedKeyPackageEvent(
        byte[] keyPackageData, List<List<string>> tags,
        string privateKeyHex, string publicKeyHex)
    {
        var content = Convert.ToBase64String(keyPackageData);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var serialized = NostrService.SerializeForEventId(publicKeyHex, createdAt, 30443, tags, content);
        var idBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(idBytes).ToLowerInvariant();

        var sig = NostrService.SignSchnorr(idBytes, Convert.FromHexString(privateKeyHex));
        var sigHex = Convert.ToHexString(sig).ToLowerInvariant();

        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", eventId);
        w.WriteString("pubkey", publicKeyHex);
        w.WriteNumber("created_at", createdAt);
        w.WriteNumber("kind", 30443);
        w.WritePropertyName("tags");
        w.WriteStartArray();
        foreach (var tag in tags)
        {
            w.WriteStartArray();
            foreach (var item in tag)
                w.WriteStringValue(item);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("content", content);
        w.WriteString("sig", sigHex);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Builds a minimal signed kind 30443 event JSON from Rust KeyPackage result.
    /// </summary>
    private string BuildRustKeyPackageEventJson(MarmotWrapper.KeyPackageResult rustKp)
    {
        var tags = rustKp.Tags;
        var content = rustKp.Content;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Convert List<List<string>> to the format SerializeForEventId expects
        var tagsList = tags.Select(t => (List<string>)t).ToList();

        var serialized = NostrService.SerializeForEventId(_rustPubKey, createdAt, 30443, tagsList, content);
        var idBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(idBytes).ToLowerInvariant();

        var sig = NostrService.SignSchnorr(idBytes, Convert.FromHexString(_rustPrivKey));
        var sigHex = Convert.ToHexString(sig).ToLowerInvariant();

        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", eventId);
        w.WriteString("pubkey", _rustPubKey);
        w.WriteNumber("created_at", createdAt);
        w.WriteNumber("kind", 30443);
        w.WritePropertyName("tags");
        w.WriteStartArray();
        foreach (var tag in tags)
        {
            w.WriteStartArray();
            foreach (var item in tag)
                w.WriteStringValue(item);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("content", content);
        w.WriteString("sig", sigHex);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
