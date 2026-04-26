using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenChat.Core.Services;
using OpenChat.Diagnostics.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Diagnostics;

/// <summary>
/// Diagnostic test that publishes a kind 30443 KeyPackage event to real relays
/// and verifies they accept and return it. Uses a dedicated test-only nsec
/// to avoid polluting relays with random keys.
/// </summary>
[Trait("Category", "Integration")]
public class KeyPackagePublishDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    // Dedicated test-only key — NOT a real user. Generated solely for this test.
    // npub: npub1testkey... — has no followers, no profile, no funds.
    private const string TestPrivateKeyHex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    private static readonly string[] TestRelays = new[]
    {
        "wss://test.thedude.cloud",
        "wss://relay.damus.io",
        "wss://nos.lol",
        "wss://relay.nostr.band"
    };

    public KeyPackagePublishDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PublishKeyPackage_Kind30443_AcceptedByRelays()
    {
        // --- Setup: real NostrService + real MLS service ---
        var nostrService = new NostrService();
        var pubKeyBytes = DerivePublicKeyFromPrivate(TestPrivateKeyHex);
        var pubKeyHex = Convert.ToHexString(pubKeyBytes).ToLowerInvariant();
        _output.WriteLine($"Test pubkey: {pubKeyHex}");

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_diag_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(TestPrivateKeyHex, pubKeyHex);

        // --- Step 1: Connect to relays ---
        _output.WriteLine("\n--- Connecting to relays ---");
        foreach (var relay in TestRelays)
        {
            try
            {
                await nostrService.ConnectAsync(relay);
                _output.WriteLine($"  Connected: {relay}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  FAILED to connect: {relay} — {ex.Message}");
            }
        }

        var connected = nostrService.ConnectedRelayUrls;
        _output.WriteLine($"\nConnected to {connected.Count} relays: {string.Join(", ", connected)}");
        Assert.True(connected.Count > 0, "Must connect to at least one relay");

        // Give relays a moment to fully establish
        await Task.Delay(2000);

        // --- Step 2: Generate a KeyPackage ---
        _output.WriteLine("\n--- Generating KeyPackage ---");
        var keyPackage = await mlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"  Data: {keyPackage.Data.Length} bytes");
        _output.WriteLine($"  Ciphersuite: 0x{keyPackage.CiphersuiteId:x4}");
        _output.WriteLine($"  Tags ({keyPackage.NostrTags.Count}):");
        foreach (var tag in keyPackage.NostrTags)
            _output.WriteLine($"    [{string.Join(", ", tag)}]");

        // Verify d-tag is present (required for kind 30443)
        var dTag = keyPackage.NostrTags.FirstOrDefault(t => t.Count >= 2 && t[0] == "d");
        Assert.NotNull(dTag);
        _output.WriteLine($"  d-tag slot: {dTag[1]}");

        // --- Step 3: Publish to relays ---
        _output.WriteLine("\n--- Publishing KeyPackage (kind 30443) ---");
        string? eventId = null;
        Exception? publishError = null;

        try
        {
            eventId = await nostrService.PublishKeyPackageAsync(
                keyPackage.Data, TestPrivateKeyHex, keyPackage.NostrTags);
            _output.WriteLine($"  Published! Event ID: {eventId}");
        }
        catch (Exception ex)
        {
            publishError = ex;
            _output.WriteLine($"  PUBLISH FAILED: {ex.Message}");
        }

        Assert.Null(publishError);
        Assert.NotNull(eventId);

        // --- Step 4: Fetch back from each relay individually ---
        _output.WriteLine("\n--- Fetching KeyPackage back from relays ---");
        var foundOnRelays = new List<string>();

        foreach (var relay in TestRelays)
        {
            var found = await FetchKind30443FromRelay(relay, pubKeyHex);
            if (found.Count > 0)
            {
                foundOnRelays.Add(relay);
                _output.WriteLine($"  {relay}: FOUND {found.Count} KeyPackage(s)");
                foreach (var ev in found)
                {
                    AnalyzeEvent(ev);
                }
            }
            else
            {
                _output.WriteLine($"  {relay}: NOT FOUND");
            }
        }

        _output.WriteLine($"\n--- Summary ---");
        _output.WriteLine($"  Published event: {eventId}");
        _output.WriteLine($"  Found on {foundOnRelays.Count}/{TestRelays.Length} relays: {string.Join(", ", foundOnRelays)}");

        Assert.True(foundOnRelays.Count > 0,
            $"KeyPackage event {eventId} was not found on any relay after publishing. " +
            "Relays may be rejecting kind 30443 events.");

        // --- Step 5: Also test via NostrService.FetchKeyPackagesAsync ---
        _output.WriteLine("\n--- Fetching via NostrService.FetchKeyPackagesAsync ---");
        var fetchedPackages = (await nostrService.FetchKeyPackagesAsync(pubKeyHex)).ToList();
        _output.WriteLine($"  Found {fetchedPackages.Count} KeyPackage(s) via service");
        foreach (var pkg in fetchedPackages)
        {
            _output.WriteLine($"    EventId: {pkg.NostrEventId?[..Math.Min(16, pkg.NostrEventId?.Length ?? 0)]}...");
            _output.WriteLine($"    Data: {pkg.Data.Length} bytes, Ciphersuite: 0x{pkg.CiphersuiteId:x4}");
            _output.WriteLine($"    Relays: {string.Join(", ", pkg.RelayUrls)}");
        }

        Assert.True(fetchedPackages.Count > 0,
            "FetchKeyPackagesAsync returned no results despite relay having the event");

        // --- Cleanup ---
        await nostrService.DisconnectAsync();
        nostrService.Dispose();

        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task PublishKeyPackage_PerRelayOkResponse()
    {
        // Simpler test: publish to each relay individually and report OK/reject per relay
        var nostrService = new NostrService();
        var pubKeyBytes = DerivePublicKeyFromPrivate(TestPrivateKeyHex);
        var pubKeyHex = Convert.ToHexString(pubKeyBytes).ToLowerInvariant();

        var dbPath = Path.Combine(Path.GetTempPath(), $"kp_diag_perrelay_{Guid.NewGuid()}.db");
        var storage = new StorageService(dbPath, new MockSecureStorage());
        await storage.InitializeAsync();

        var mlsService = new ManagedMlsService(storage);
        await mlsService.InitializeAsync(TestPrivateKeyHex, pubKeyHex);

        var keyPackage = await mlsService.GenerateKeyPackageAsync();
        _output.WriteLine($"KeyPackage: {keyPackage.Data.Length} bytes, {keyPackage.NostrTags.Count} tags");

        _output.WriteLine("\n--- Per-relay publish test ---");
        var results = new List<(string relay, bool ok, string? reason)>();

        foreach (var relay in TestRelays)
        {
            var result = await PublishToSingleRelay(relay, pubKeyHex, keyPackage, TestPrivateKeyHex);
            results.Add(result);
            var status = result.ok ? "ACCEPTED" : $"REJECTED ({result.reason})";
            _output.WriteLine($"  {relay}: {status}");
        }

        _output.WriteLine($"\n--- Results ---");
        var accepted = results.Count(r => r.ok);
        var rejected = results.Count(r => !r.ok);
        _output.WriteLine($"  Accepted: {accepted}/{results.Count}");
        _output.WriteLine($"  Rejected: {rejected}/{results.Count}");

        foreach (var (relay, ok, reason) in results.Where(r => !r.ok))
            _output.WriteLine($"  FAILED: {relay} — {reason}");

        Assert.True(accepted > 0, "No relay accepted the kind 30443 event");

        try { File.Delete(dbPath); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<(string relay, bool ok, string? reason)> PublishToSingleRelay(
        string relayUrl, string pubKeyHex, Core.Models.KeyPackage keyPackage, string privateKeyHex)
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            // Build and sign the event
            var tags = keyPackage.NostrTags;
            var content = Convert.ToBase64String(keyPackage.Data);
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var eventJson = BuildSignedEvent(30443, content, tags, createdAt, privateKeyHex, pubKeyHex);
            var eventMessage = $"[\"EVENT\",{eventJson}]";

            await ws.SendAsync(Encoding.UTF8.GetBytes(eventMessage),
                WebSocketMessageType.Text, true, cts.Token);

            // Wait for OK response
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.Contains("\"OK\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    var arr = doc.RootElement;
                    if (arr.GetArrayLength() >= 3 && arr[0].GetString() == "OK")
                    {
                        var accepted = arr[2].GetBoolean();
                        var reason = arr.GetArrayLength() >= 4 ? arr[3].GetString() : null;
                        return (relayUrl, accepted, reason);
                    }
                }
            }

            return (relayUrl, false, "no OK response received");
        }
        catch (Exception ex)
        {
            return (relayUrl, false, $"connection error: {ex.Message}");
        }
    }

    private async Task<List<string>> FetchKind30443FromRelay(string relayUrl, string pubKeyHex)
    {
        var events = new List<string>();
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);

            var subId = $"kpd_{Guid.NewGuid():N}"[..12];
            var filterJson = $"{{\"kinds\":[30443],\"authors\":[\"{pubKeyHex}\"],\"limit\":5}}";
            var req = $"[\"REQ\",\"{subId}\",{filterJson}]";

            await ws.SendAsync(Encoding.UTF8.GetBytes(req),
                WebSocketMessageType.Text, true, cts.Token);

            var buf = new byte[65536];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var msg = sb.ToString();
                sb.Clear();

                if (msg.StartsWith("[\"EOSE\"")) break;
                if (msg.StartsWith("[\"EVENT\""))
                {
                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.GetArrayLength() >= 3)
                        events.Add(doc.RootElement[2].GetRawText());
                }
            }

            // Send CLOSE
            var closeMsg = $"[\"CLOSE\",\"{subId}\"]";
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes(closeMsg),
                    WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"    Fetch error from {relayUrl}: {ex.Message}");
        }
        return events;
    }

    private void AnalyzeEvent(string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var ev = doc.RootElement;
        var id = ev.GetProperty("id").GetString()!;
        var kind = ev.GetProperty("kind").GetInt32();
        var createdAt = ev.GetProperty("created_at").GetInt64();
        var content = ev.GetProperty("content").GetString() ?? "";

        _output.WriteLine($"    Event: {id[..16]}..., kind={kind}");
        _output.WriteLine($"    created_at: {DateTimeOffset.FromUnixTimeSeconds(createdAt):u}");
        _output.WriteLine($"    content: {content.Length} chars (base64)");

        try
        {
            var data = Convert.FromBase64String(content);
            _output.WriteLine($"    decoded: {data.Length} bytes");
        }
        catch { _output.WriteLine($"    content is NOT valid base64"); }

        foreach (var tag in ev.GetProperty("tags").EnumerateArray())
        {
            var parts = new List<string>();
            foreach (var item in tag.EnumerateArray()) parts.Add(item.GetString() ?? "");
            _output.WriteLine($"    tag: [{string.Join(", ", parts)}]");
        }
    }

    private static string BuildSignedEvent(int kind, string content, List<List<string>> tags,
        long createdAt, string privateKeyHex, string pubKeyHex)
    {
        // Build NIP-01 canonical serialization: [0, pubkey, created_at, kind, tags, content]
        var serialized = NostrService.SerializeForEventId(pubKeyHex, createdAt, kind, tags, content);
        var eventIdBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var eventId = Convert.ToHexString(eventIdBytes).ToLowerInvariant();
        var signature = NostrService.SignSchnorr(eventIdBytes, Convert.FromHexString(privateKeyHex));
        var signatureHex = Convert.ToHexString(signature).ToLowerInvariant();

        // Build event JSON
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventId);
            writer.WriteString("pubkey", pubKeyHex);
            writer.WriteNumber("created_at", createdAt);
            writer.WriteNumber("kind", kind);
            writer.WriteStartArray("tags");
            foreach (var tag in tags)
            {
                writer.WriteStartArray();
                foreach (var v in tag) writer.WriteStringValue(v);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("content", content);
            writer.WriteString("sig", signatureHex);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] DerivePublicKeyFromPrivate(string privateKeyHex)
    {
        var privBytes = Convert.FromHexString(privateKeyHex);
        if (!NBitcoin.Secp256k1.Context.Instance.TryCreateECPrivKey(privBytes, out var ecPrivKey) || ecPrivKey is null)
            throw new InvalidOperationException("Invalid private key");
        var pubKey = ecPrivKey.CreateXOnlyPubKey();
        var pubBytes = new byte[32];
        pubKey.WriteToSpan(pubBytes);
        return pubBytes;
    }
}
