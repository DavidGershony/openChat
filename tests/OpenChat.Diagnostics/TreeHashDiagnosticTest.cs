using System.Reflection;
using System.Text;
using System.Text.Json;
using DotnetMls.Codec;
using DotnetMls.Group;
using DotnetMls.Tree;
using OpenChat.Core.Configuration;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Diagnostics.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OpenChat.Diagnostics;

/// <summary>
/// Diagnostic: dumps tree_hash, epoch_authenticator, exporter_secret, and ratchet tree
/// after CreateGroup + AddMember so they can be compared with ts-mls values.
/// This is the key test for finding where the key schedule diverges.
/// </summary>
public class TreeHashDiagnosticTest
{
    private readonly ITestOutputHelper _output;

    public TreeHashDiagnosticTest(ITestOutputHelper output) => _output = output;

    private static MlsGroup GetMlsGroup(ManagedMlsService mlsService, byte[] groupId)
    {
        // Access the private _mdk field, then its _groups dictionary via reflection
        var mdkField = typeof(ManagedMlsService).GetField("_mdk", BindingFlags.NonPublic | BindingFlags.Instance);
        var mdk = mdkField!.GetValue(mlsService);
        var groupsField = mdk!.GetType().GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance);
        var groups = (Dictionary<string, MlsGroup>)groupsField!.GetValue(mdk)!;
        var hex = Convert.ToHexString(groupId).ToUpperInvariant();
        return groups[hex];
    }

    [Fact]
    public async Task DumpGroupState_AfterCreateAndAddMember()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);

        // Create Alice and Bob
        var nostrA = new NostrService();
        var nostrB = new NostrService();
        var keysA = nostrA.GenerateKeyPair();
        var keysB = nostrB.GenerateKeyPair();

        var dbA = Path.Combine(Path.GetTempPath(), $"diag_a_{Guid.NewGuid():N}.db");
        var dbB = Path.Combine(Path.GetTempPath(), $"diag_b_{Guid.NewGuid():N}.db");

        var storageA = new StorageService(dbA, new MockSecureStorage());
        var storageB = new StorageService(dbB, new MockSecureStorage());
        await storageA.InitializeAsync();
        await storageB.InitializeAsync();
        await storageA.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(), PublicKeyHex = keysA.publicKeyHex,
            PrivateKeyHex = keysA.privateKeyHex, Npub = keysA.npub, Nsec = keysA.nsec,
            DisplayName = "Alice", IsCurrentUser = true, CreatedAt = DateTime.UtcNow
        });
        await storageB.SaveCurrentUserAsync(new User
        {
            Id = Guid.NewGuid().ToString(), PublicKeyHex = keysB.publicKeyHex,
            PrivateKeyHex = keysB.privateKeyHex, Npub = keysB.npub, Nsec = keysB.nsec,
            DisplayName = "Bob", IsCurrentUser = true, CreatedAt = DateTime.UtcNow
        });

        var mlsA = new ManagedMlsService(storageA);
        var mlsB = new ManagedMlsService(storageB);
        await mlsA.InitializeAsync(keysA.privateKeyHex, keysA.publicKeyHex);
        await mlsB.InitializeAsync(keysB.privateKeyHex, keysB.publicKeyHex);

        _output.WriteLine($"Alice pubkey: {keysA.publicKeyHex}");
        _output.WriteLine($"Bob pubkey:   {keysB.publicKeyHex}");

        // Alice creates group
        var groupInfo = await mlsA.CreateGroupAsync("Diag Group", new[] { "wss://relay.test" });
        _output.WriteLine($"\n=== GROUP CREATED (epoch 0) ===");
        _output.WriteLine($"GroupId: {Convert.ToHexString(groupInfo.GroupId).ToLowerInvariant()}");

        var aliceGroup0 = GetMlsGroup(mlsA, groupInfo.GroupId);
        _output.WriteLine($"Epoch: {aliceGroup0.Epoch}");
        _output.WriteLine($"TreeHash: {Convert.ToHexString(aliceGroup0.GroupContext.TreeHash).ToLowerInvariant()}");
        _output.WriteLine($"EpochAuthenticator: {Convert.ToHexString(aliceGroup0.KeySchedule.EpochAuthenticator).ToLowerInvariant()}");
        _output.WriteLine($"ExporterSecret: {Convert.ToHexString(aliceGroup0.KeySchedule.ExporterSecret).ToLowerInvariant()}");

        // Dump the ratchet tree
        var tree0 = aliceGroup0.Tree;
        byte[] treeBytes0 = TlsCodec.Serialize(w => tree0.WriteTo(w));
        _output.WriteLine($"RatchetTree: {treeBytes0.Length} bytes");
        _output.WriteLine($"RatchetTree (hex): {Convert.ToHexString(treeBytes0).ToLowerInvariant()}");

        // Bob generates KeyPackage
        var kpBob = await mlsB.GenerateKeyPackageAsync();
        var kpContent = Convert.ToBase64String(kpBob.Data);
        var fakeEventJson = $"{{\"id\":\"fake\",\"pubkey\":\"{keysB.publicKeyHex}\",\"created_at\":0,\"kind\":443,\"tags\":[],\"content\":\"{kpContent}\",\"sig\":\"fake\"}}";
        var kp = new KeyPackage
        {
            Id = Guid.NewGuid().ToString(), Data = kpBob.Data,
            NostrEventId = "fake_kp_event", OwnerPublicKey = keysB.publicKeyHex,
            EventJson = fakeEventJson, CreatedAt = DateTime.UtcNow
        };

        // Dump Bob's KeyPackage private keys for ts-mls cross-impl test
        var storedKpField = typeof(ManagedMlsService).GetField("_storedKeyPackages", BindingFlags.NonPublic | BindingFlags.Instance);
        var storedKps = storedKpField!.GetValue(mlsB) as System.Collections.IList;
        var lastKp = storedKps![storedKps.Count - 1];
        var initPrivProp = lastKp!.GetType().GetProperty("InitPrivateKey");
        var hpkePrivProp = lastKp.GetType().GetProperty("HpkePrivateKey");
        var bobInitPriv = (byte[])initPrivProp!.GetValue(lastKp)!;
        var bobHpkePriv = (byte[])hpkePrivProp!.GetValue(lastKp)!;
        _output.WriteLine($"Bob InitPrivateKey: {Convert.ToHexString(bobInitPriv).ToLowerInvariant()}");
        _output.WriteLine($"Bob HpkePrivateKey: {Convert.ToHexString(bobHpkePriv).ToLowerInvariant()}");
        _output.WriteLine($"Bob KeyPackage (hex): {Convert.ToHexString(kpBob.Data).ToLowerInvariant()}");

        // Alice adds Bob
        _output.WriteLine($"\n=== ALICE ADDS BOB (epoch 0 → 1) ===");
        var welcome = await mlsA.AddMemberAsync(groupInfo.GroupId, kp);

        var aliceGroup1 = GetMlsGroup(mlsA, groupInfo.GroupId);
        _output.WriteLine($"Epoch: {aliceGroup1.Epoch}");
        _output.WriteLine($"TreeHash: {Convert.ToHexString(aliceGroup1.GroupContext.TreeHash).ToLowerInvariant()}");
        _output.WriteLine($"ConfirmedTranscriptHash: {Convert.ToHexString(aliceGroup1.GroupContext.ConfirmedTranscriptHash).ToLowerInvariant()}");
        _output.WriteLine($"EpochAuthenticator: {Convert.ToHexString(aliceGroup1.KeySchedule.EpochAuthenticator).ToLowerInvariant()}");
        _output.WriteLine($"ExporterSecret: {Convert.ToHexString(aliceGroup1.KeySchedule.ExporterSecret).ToLowerInvariant()}");

        var tree1 = aliceGroup1.Tree;
        byte[] treeBytes1 = TlsCodec.Serialize(w => tree1.WriteTo(w));
        _output.WriteLine($"RatchetTree: {treeBytes1.Length} bytes");
        _output.WriteLine($"RatchetTree (hex): {Convert.ToHexString(treeBytes1).ToLowerInvariant()}");

        // Dump Alice's leaf node (index 0) details
        var aliceLeaf = tree1.GetLeaf(0);
        _output.WriteLine($"\nAlice leaf (index 0):");
        _output.WriteLine($"  Source: {aliceLeaf!.Source}");
        _output.WriteLine($"  EncryptionKey: {Convert.ToHexString(aliceLeaf.EncryptionKey).ToLowerInvariant()}");
        _output.WriteLine($"  SignatureKey: {Convert.ToHexString(aliceLeaf.SignatureKey).ToLowerInvariant()}");
        _output.WriteLine($"  ParentHash: {Convert.ToHexString(aliceLeaf.ParentHash).ToLowerInvariant()}");
        _output.WriteLine($"  Signature: {Convert.ToHexString(aliceLeaf.Signature).ToLowerInvariant()}");

        // Dump Welcome bytes
        _output.WriteLine($"\nWelcome: {welcome.WelcomeData.Length} bytes");
        _output.WriteLine($"Welcome (hex): {Convert.ToHexString(welcome.WelcomeData).ToLowerInvariant()}");

        // Write JSON for ts-mls cross-impl test
        var crossImplData = new
        {
            welcome_hex = Convert.ToHexString(welcome.WelcomeData).ToLowerInvariant(),
            bob_kp_hex = Convert.ToHexString(kpBob.Data).ToLowerInvariant(),
            bob_init_priv_hex = Convert.ToHexString(bobInitPriv).ToLowerInvariant(),
            bob_hpke_priv_hex = Convert.ToHexString(bobHpkePriv).ToLowerInvariant(),
            expected_tree_hash = Convert.ToHexString(aliceGroup1.GroupContext.TreeHash).ToLowerInvariant(),
            expected_exporter_secret = Convert.ToHexString(aliceGroup1.KeySchedule.ExporterSecret).ToLowerInvariant(),
            expected_epoch_authenticator = Convert.ToHexString(aliceGroup1.KeySchedule.EpochAuthenticator).ToLowerInvariant(),
            expected_confirmed_transcript_hash = Convert.ToHexString(aliceGroup1.GroupContext.ConfirmedTranscriptHash).ToLowerInvariant(),
        };
        var jsonPath = Path.Combine(Path.GetTempPath(), "dotnetmls-cross-impl-data.json");
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(crossImplData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine($"\nCross-impl data written to: {jsonPath}");

        // Bob processes Welcome
        _output.WriteLine($"\n=== BOB PROCESSES WELCOME ===");
        var bobGroupInfo = await mlsB.ProcessWelcomeAsync(welcome.WelcomeData,
            "0000000000000000000000000000000000000000000000000000000000000000");

        var bobGroup = GetMlsGroup(mlsB, bobGroupInfo.GroupId);
        _output.WriteLine($"Bob Epoch: {bobGroup.Epoch}");
        _output.WriteLine($"Bob TreeHash: {Convert.ToHexString(bobGroup.GroupContext.TreeHash).ToLowerInvariant()}");
        _output.WriteLine($"Bob ConfirmedTranscriptHash: {Convert.ToHexString(bobGroup.GroupContext.ConfirmedTranscriptHash).ToLowerInvariant()}");
        _output.WriteLine($"Bob EpochAuthenticator: {Convert.ToHexString(bobGroup.KeySchedule.EpochAuthenticator).ToLowerInvariant()}");
        _output.WriteLine($"Bob ExporterSecret: {Convert.ToHexString(bobGroup.KeySchedule.ExporterSecret).ToLowerInvariant()}");

        // Compare
        bool treeHashMatch = aliceGroup1.GroupContext.TreeHash.AsSpan()
            .SequenceEqual(bobGroup.GroupContext.TreeHash);
        bool exporterMatch = aliceGroup1.KeySchedule.ExporterSecret.AsSpan()
            .SequenceEqual(bobGroup.KeySchedule.ExporterSecret);
        bool authMatch = aliceGroup1.KeySchedule.EpochAuthenticator.AsSpan()
            .SequenceEqual(bobGroup.KeySchedule.EpochAuthenticator);

        _output.WriteLine($"\n=== COMPARISON ===");
        _output.WriteLine($"TreeHash match:          {(treeHashMatch ? "YES" : "NO")}");
        _output.WriteLine($"ExporterSecret match:    {(exporterMatch ? "YES" : "NO")}");
        _output.WriteLine($"EpochAuthenticator match:{(authMatch ? "YES" : "NO")}");

        // Message round-trip
        _output.WriteLine($"\n=== MESSAGE ROUND-TRIP ===");
        var ct = await mlsA.EncryptMessageAsync(groupInfo.GroupId, "hello from alice");
        using var doc = JsonDocument.Parse(ct);
        var content = doc.RootElement.GetProperty("content").GetString()!;
        var ctBytes = Convert.FromBase64String(content);
        _output.WriteLine($"Ciphertext: {ctBytes.Length} bytes");

        var decrypted = await mlsB.DecryptMessageAsync(bobGroupInfo.GroupId, ctBytes);
        _output.WriteLine($"Decrypted: \"{decrypted.Plaintext}\"");

        Assert.Equal("hello from alice", decrypted.Plaintext);
        Assert.True(exporterMatch, "Exporter secrets diverged between Alice and Bob");

        // Cleanup
        try { File.Delete(dbA); } catch { }
        try { File.Delete(dbB); } catch { }
    }
}
