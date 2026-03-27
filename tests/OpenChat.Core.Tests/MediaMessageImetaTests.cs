using System.Text.Json;
using OpenChat.Core.Configuration;
using OpenChat.Core.Services;
using OpenChat.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Core.Tests;

/// <summary>
/// Verifies that MLS-encrypted media messages include imeta tags in the rumor
/// so cross-implementation clients can download and decrypt the media.
/// </summary>
public class MediaMessageImetaTests
{
    private readonly ITestOutputHelper _output;

    public MediaMessageImetaTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// When a media message is sent, the encrypted rumor (kind 9) must include
    /// an imeta tag with the URL, mime type, SHA-256 hash, nonce, and encryption
    /// version. Without this, the web client only sees placeholder text like
    /// "[Encrypted image: file.jpg]".
    /// </summary>
    [Fact]
    public async Task EncryptMessageAsync_WithMediaTags_IncludesImetaInRumor()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);

        var dbPath = Path.Combine(Path.GetTempPath(), $"imeta_test_{Guid.NewGuid():N}.db");
        try
        {
            var storage = new StorageService(dbPath, new MockSecureStorage());
            await storage.InitializeAsync();

            var mlsA = new ManagedMlsService(storage);
            var mlsB = new ManagedMlsService(storage);

            var nostr = new NostrService();
            var keysA = nostr.GenerateKeyPair();
            var keysB = nostr.GenerateKeyPair();

            await mlsA.InitializeAsync(keysA.privateKeyHex, keysA.publicKeyHex);
            await mlsB.InitializeAsync(keysB.privateKeyHex, keysB.publicKeyHex);

            // Create group and add member
            var group = await mlsA.CreateGroupAsync("Test", new[] { "wss://relay.test" });
            var kpB = await mlsB.GenerateKeyPackageAsync();
            var fakeEvent = $"{{\"id\":\"fake\",\"pubkey\":\"{keysB.publicKeyHex}\",\"created_at\":0,\"kind\":443,\"tags\":[],\"content\":\"{Convert.ToBase64String(kpB.Data)}\",\"sig\":\"fake\"}}";
            var kp = new OpenChat.Core.Models.KeyPackage
            {
                Id = Guid.NewGuid().ToString(), Data = kpB.Data,
                NostrEventId = "fake", OwnerPublicKey = keysB.publicKeyHex,
                EventJson = fakeEvent, CreatedAt = DateTime.UtcNow
            };
            var welcome = await mlsA.AddMemberAsync(group.GroupId, kp);
            await mlsB.ProcessWelcomeAsync(welcome.WelcomeData, new string('0', 64));

            // Build imeta tags for a media message
            var mediaUrl = "https://blossom.primal.net/abc123def456";
            var sha256 = "a0528807e6b22980";
            var nonce = "deadbeef12345678abcd";
            var mimeType = "image/jpeg";
            var filename = "photo.jpg";

            var tags = new List<List<string>>
            {
                new() { "imeta",
                    $"url {mediaUrl}",
                    $"m {mimeType}",
                    $"x {sha256}",
                    $"n {nonce}",
                    $"v mip04-v2",
                    $"filename {filename}" }
            };

            // Content is empty per MIP-04 — metadata is in imeta tags
            var eventJson = await mlsA.EncryptMessageAsync(
                group.GroupId, "", tags);

            // Decrypt on other side
            using var doc = JsonDocument.Parse(eventJson);
            var content = doc.RootElement.GetProperty("content").GetString()!;
            var ctBytes = Convert.FromBase64String(content);

            var decrypted = await mlsB.DecryptMessageAsync(group.GroupId, ctBytes);
            _output.WriteLine($"Decrypted content: {decrypted.Plaintext}");

            // Parse the decrypted rumor to check tags
            Assert.NotNull(decrypted.RumorJson);
            using var rumorDoc = JsonDocument.Parse(decrypted.RumorJson!);
            var rumor = rumorDoc.RootElement;

            var rumorTags = rumor.GetProperty("tags");
            _output.WriteLine($"Rumor tags count: {rumorTags.GetArrayLength()}");

            Assert.True(rumorTags.GetArrayLength() > 0,
                "Rumor must have tags — the imeta tag with media metadata is missing. " +
                "The web client needs imeta tags to download and decrypt the media file.");

            // Find the imeta tag
            bool foundImeta = false;
            for (int i = 0; i < rumorTags.GetArrayLength(); i++)
            {
                var tag = rumorTags[i];
                if (tag.GetArrayLength() > 0 && tag[0].GetString() == "imeta")
                {
                    foundImeta = true;
                    _output.WriteLine($"Found imeta tag with {tag.GetArrayLength()} entries");

                    // Verify it contains the expected fields
                    var tagValues = new List<string>();
                    for (int j = 0; j < tag.GetArrayLength(); j++)
                        tagValues.Add(tag[j].GetString() ?? "");

                    Assert.Contains(tagValues, v => v.StartsWith("url "));
                    Assert.Contains(tagValues, v => v.StartsWith("m "));
                    Assert.Contains(tagValues, v => v.StartsWith("x "));
                    Assert.Contains(tagValues, v => v.StartsWith("n ") && !v.StartsWith("n ") == false); // nonce
                    Assert.Contains(tagValues, v => v.StartsWith("v ")); // version
                    Assert.Contains(tagValues, v => v.StartsWith("filename ")); // filename
                    break;
                }
            }

            Assert.True(foundImeta, "imeta tag not found in decrypted rumor");
            _output.WriteLine("PASS: imeta tag present in encrypted rumor");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
