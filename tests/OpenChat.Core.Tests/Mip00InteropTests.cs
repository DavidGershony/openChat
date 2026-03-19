using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetMls.Crypto;
using DotnetMls.Types;
using MarmotCs.Protocol.Mip00;
using OpenChat.Core.Marmot;
using OpenChat.Core.Services;
using Xunit;
using OpenChat.Core.Tests.TestHelpers;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// MIP-00 interop tests verifying that C# and Rust MDK implementations
/// produce compatible kind 443 KeyPackage events.
///
/// These tests exercise the native DLL directly (no relay needed).
/// Requires: openchat_native.dll in test output directory.
/// </summary>
[Trait("Category", "Native")]
public class Mip00InteropTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // Rust MDK client
    private MarmotWrapper _rustClient = null!;
    private string _rustPrivKey = null!;
    private string _rustPubKey = null!;

    // C# MDK client
    private ManagedMlsService _managedMls = null!;
    private StorageService _storage = null!;
    private string _managedPrivKey = null!;
    private string _managedPubKey = null!;
    private string _dbPath = null!;

    public Mip00InteropTests(ITestOutputHelper output)
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
        (_managedPrivKey, _managedPubKey, _, _) = nostrService.GenerateKeyPair();

        // Initialize Rust MDK
        _rustClient = new MarmotWrapper();
        await _rustClient.InitializeAsync(_rustPrivKey, _rustPubKey);

        // Initialize C# MDK
        _dbPath = Path.Combine(Path.GetTempPath(), $"mip00_interop_{Guid.NewGuid()}.db");
        _storage = new StorageService(_dbPath, new MockSecureStorage());
        await _storage.InitializeAsync();
        _managedMls = new ManagedMlsService(_storage);
        await _managedMls.InitializeAsync(_managedPrivKey, _managedPubKey);
    }

    public async Task DisposeAsync()
    {
        _rustClient?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        TryDeleteFile(_dbPath);
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 1: C# KeyPackage accepted by Rust add_member
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a kind 443 event built by marmot-cs KeyPackageEventBuilder
    /// (with all MIP-00 tags) is accepted by the Rust MDK's add_member.
    /// This is the core MIP-00 interop test — no relay needed.
    /// </summary>
    [SkippableFact]
    public async Task CSharpKeyPackage_AcceptedByRustAddMember()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        // Generate KeyPackage from C# MDK
        var keyPackage = await _managedMls.GenerateKeyPackageAsync();
        Assert.True(keyPackage.Data.Length >= 64);
        _output.WriteLine($"C# KeyPackage: {keyPackage.Data.Length} bytes, {keyPackage.NostrTags?.Count} tags");

        // Populate relays tag (the builder leaves it empty; PublishKeyPackageAsync would fill it)
        var tags = keyPackage.NostrTags!;
        var relaysTag = tags.FirstOrDefault(t => t.Count >= 1 && t[0] == "relays");
        if (relaysTag != null && relaysTag.Count <= 1)
            relaysTag.Add("wss://relay.example.com");

        // Build a signed kind 443 Nostr event from the C# KeyPackage
        var eventJson = BuildSignedKeyPackageEvent(
            keyPackage.Data, tags, _managedPrivKey, _managedPubKey);
        _output.WriteLine($"Event JSON ({eventJson.Length} chars): {eventJson[..Math.Min(200, eventJson.Length)]}...");

        // Verify the event JSON has all required MIP-00 tags
        using var doc = JsonDocument.Parse(eventJson);
        var root = doc.RootElement;
        Assert.Equal(443, root.GetProperty("kind").GetInt32());
        var jsonTags = root.GetProperty("tags");
        AssertHasTag(jsonTags, "encoding", "base64");
        AssertHasTag(jsonTags, "mls_protocol_version", "1.0");
        AssertHasTag(jsonTags, "mls_ciphersuite", "0x0001");
        AssertHasTagName(jsonTags, "mls_extensions");
        AssertHasTagName(jsonTags, "i");

        // Create a group with Rust MDK
        var (groupId, _) = await _rustClient.CreateGroupAsync("MIP-00 Interop Group");
        _output.WriteLine($"Rust group created: {Convert.ToHexString(groupId).ToLowerInvariant()[..16]}...");

        // Add C# member using the signed event JSON — this is the critical interop check
        var eventJsonBytes = Encoding.UTF8.GetBytes(eventJson);
        var result = await _rustClient.AddMemberAsync(groupId, eventJsonBytes);
        Assert.NotNull(result.WelcomeData);
        Assert.True(result.WelcomeData.Length > 0);
        _output.WriteLine($"Rust accepted C# KeyPackage: welcome={result.WelcomeData.Length} bytes");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 2: Rust KeyPackage parsed by C# KeyPackageEventParser
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a KeyPackage event generated by the Rust MDK can be
    /// correctly parsed by marmot-cs KeyPackageEventParser.
    /// </summary>
    [SkippableFact]
    public async Task RustKeyPackage_ParsedByCSharpParser()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        // Generate KeyPackage from Rust MDK
        var rustKp = await _rustClient.GenerateKeyPackageAsync();
        Assert.NotEmpty(rustKp.Content);
        Assert.True(rustKp.Tags.Count >= 3, $"Expected >= 3 tags, got {rustKp.Tags.Count}");
        _output.WriteLine($"Rust KeyPackage: content={rustKp.Content.Length} chars, {rustKp.Tags.Count} tags");

        foreach (var tag in rustKp.Tags)
            _output.WriteLine($"  Rust tag: [{string.Join(", ", tag)}]");

        // Convert Rust tags (List<List<string>>) to string[][] for parser
        var tags = rustKp.Tags.Select(t => t.ToArray()).ToArray();

        // Parse with C# parser — should not throw
        var (kpBytes, kpRefHex, relays) = KeyPackageEventParser.ParseKeyPackageEvent(rustKp.Content, tags);

        Assert.NotEmpty(kpBytes);
        Assert.NotEmpty(kpRefHex);
        _output.WriteLine($"C# parsed Rust KeyPackage: {kpBytes.Length} bytes, ref={kpRefHex[..16]}...");

        // Verify the decoded bytes match the base64 content
        var expectedBytes = Convert.FromBase64String(rustKp.Content);
        Assert.Equal(expectedBytes, kpBytes);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 3: KeyPackageRef matches between C# and Rust
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the KeyPackageRef (i tag) computed by the Rust MDK matches
    /// the KeyPackageRef computed by C# (dotnet-mls) for the same KeyPackage bytes.
    /// Both should use RefHash("MLS 1.0 KeyPackage Reference", keyPackageBytes).
    /// </summary>
    [SkippableFact]
    public async Task KeyPackageRef_MatchesBetweenCSharpAndRust()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        // Generate KeyPackage from Rust MDK
        var rustKp = await _rustClient.GenerateKeyPackageAsync();
        var kpBytes = Convert.FromBase64String(rustKp.Content);

        // Extract the i tag (KeyPackageRef) from Rust output
        var rustITag = rustKp.Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == "i");
        Assert.NotNull(rustITag);
        var rustKpRef = rustITag[1];
        _output.WriteLine($"Rust KeyPackageRef: {rustKpRef}");

        // Compute KeyPackageRef in C# using the same bytes
        var cs = new CipherSuite0x0001();
        var csharpKpRef = KeyPackageRef.Compute(cs, kpBytes);
        var csharpKpRefHex = Convert.ToHexString(csharpKpRef.Value).ToLowerInvariant();
        _output.WriteLine($"C# KeyPackageRef:   {csharpKpRefHex}");

        // They must match — this proves both implementations use the same hash label
        Assert.Equal(rustKpRef, csharpKpRefHex);
        _output.WriteLine("KeyPackageRef MATCHES between Rust and C#");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 4: C# KeyPackage roundtrip — build, sign, parse
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a KeyPackage built by C# ManagedMlsService, serialized as
    /// a signed kind 443 event, and then parsed back by KeyPackageEventParser
    /// produces the same bytes and KeyPackageRef.
    /// </summary>
    [SkippableFact]
    public async Task CSharpKeyPackage_SignedEvent_RoundTrips()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        // Generate KeyPackage from C# MDK
        var keyPackage = await _managedMls.GenerateKeyPackageAsync();

        // Build signed event
        var eventJson = BuildSignedKeyPackageEvent(
            keyPackage.Data, keyPackage.NostrTags!, _managedPrivKey, _managedPubKey);

        // Parse the event JSON to extract tags
        using var doc = JsonDocument.Parse(eventJson);
        var root = doc.RootElement;
        var content = root.GetProperty("content").GetString()!;
        var tagsJson = root.GetProperty("tags");

        var tags = new List<string[]>();
        foreach (var tagArr in tagsJson.EnumerateArray())
        {
            var tag = new List<string>();
            foreach (var item in tagArr.EnumerateArray())
                tag.Add(item.GetString() ?? "");
            tags.Add(tag.ToArray());
        }

        // Parse with KeyPackageEventParser
        var (parsedBytes, parsedRef, _) = KeyPackageEventParser.ParseKeyPackageEvent(
            content, tags.ToArray());

        Assert.Equal(keyPackage.Data, parsedBytes);

        // Verify KeyPackageRef matches
        var cs = new CipherSuite0x0001();
        var expectedRef = Convert.ToHexString(
            KeyPackageRef.Compute(cs, keyPackage.Data).Value).ToLowerInvariant();
        Assert.Equal(expectedRef, parsedRef);

        _output.WriteLine($"Roundtrip OK: {parsedBytes.Length} bytes, ref={parsedRef[..16]}...");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test 5: Rust tag names match MIP-00 spec
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the Rust MDK produces kind 443 events with the correct
    /// MIP-00 tag names (mls_protocol_version, mls_ciphersuite, etc).
    /// </summary>
    [SkippableFact]
    public async Task RustKeyPackage_HasCorrectMip00TagNames()
    {
        Skip.IfNot(NativeDllAvailable(), "Native DLL not available");

        var rustKp = await _rustClient.GenerateKeyPackageAsync();
        var tagNames = rustKp.Tags.Select(t => t[0]).ToList();

        _output.WriteLine($"Rust tag names: {string.Join(", ", tagNames)}");

        Assert.Contains("encoding", tagNames);
        Assert.Contains("mls_protocol_version", tagNames);
        Assert.Contains("mls_ciphersuite", tagNames);
        Assert.Contains("i", tagNames);

        // Verify tag values
        var encodingTag = rustKp.Tags.First(t => t[0] == "encoding");
        Assert.Equal("base64", encodingTag[1]);

        var protoTag = rustKp.Tags.First(t => t[0] == "mls_protocol_version");
        Assert.Equal("1.0", protoTag[1]);

        var csTag = rustKp.Tags.First(t => t[0] == "mls_ciphersuite");
        Assert.Equal("0x0001", csTag[1]);

        _output.WriteLine("All MIP-00 tag names and values match spec");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a fully signed kind 443 Nostr event JSON from KeyPackage bytes and tags.
    /// This simulates what NostrService.PublishKeyPackageAsync does internally.
    /// </summary>
    private static string BuildSignedKeyPackageEvent(
        byte[] keyPackageData, List<List<string>> tags,
        string privateKeyHex, string publicKeyHex)
    {
        var content = Convert.ToBase64String(keyPackageData);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Compute event ID: SHA-256 of [0, pubkey, created_at, kind, tags, content]
        var serialized = NostrService.SerializeForEventId(publicKeyHex, createdAt, 443, tags, content);
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(idBytes).ToLowerInvariant();

        // Sign with Schnorr (BIP-340)
        var sig = NostrService.SignSchnorr(idBytes, Convert.FromHexString(privateKeyHex));
        var sigHex = Convert.ToHexString(sig).ToLowerInvariant();

        // Build the event JSON object
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("id", eventId);
        w.WriteString("pubkey", publicKeyHex);
        w.WriteNumber("created_at", createdAt);
        w.WriteNumber("kind", 443);
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

    private static void AssertHasTag(JsonElement tags, string name, string expectedValue)
    {
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.GetArrayLength() >= 2 &&
                tag[0].GetString() == name &&
                tag[1].GetString() == expectedValue)
                return;
        }
        Assert.Fail($"Missing tag [{name}, {expectedValue}]");
    }

    private static void AssertHasTagName(JsonElement tags, string name)
    {
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.GetArrayLength() >= 1 && tag[0].GetString() == name)
                return;
        }
        Assert.Fail($"Missing tag [{name}]");
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
