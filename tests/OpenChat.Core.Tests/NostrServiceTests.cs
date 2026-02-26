using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class NostrServiceTests
{
    private readonly NostrService _nostrService;

    public NostrServiceTests()
    {
        _nostrService = new NostrService();
    }

    [Fact]
    public void GenerateKeyPair_ShouldReturnValidKeys()
    {
        // Act
        var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.GenerateKeyPair();

        // Assert
        Assert.NotEmpty(privateKeyHex);
        Assert.NotEmpty(publicKeyHex);
        Assert.StartsWith("nsec", nsec);
        Assert.StartsWith("npub", npub);
        Assert.Equal(64, privateKeyHex.Length); // 32 bytes = 64 hex chars
        Assert.Equal(64, publicKeyHex.Length);
    }

    [Fact]
    public void GenerateKeyPair_ShouldGenerateDifferentKeysEachTime()
    {
        // Act
        var (privateKey1, _, _, _) = _nostrService.GenerateKeyPair();
        var (privateKey2, _, _, _) = _nostrService.GenerateKeyPair();

        // Assert
        Assert.NotEqual(privateKey1, privateKey2);
    }

    [Fact]
    public void ImportPrivateKey_WithHex_ShouldWork()
    {
        // Arrange
        var (originalPrivateKeyHex, _, _, _) = _nostrService.GenerateKeyPair();

        // Act
        var (importedPrivateKeyHex, _, nsec, npub) = _nostrService.ImportPrivateKey(originalPrivateKeyHex);

        // Assert
        Assert.Equal(originalPrivateKeyHex, importedPrivateKeyHex);
        Assert.StartsWith("nsec", nsec);
        Assert.StartsWith("npub", npub);
    }

    [Fact]
    public void ImportPrivateKey_WithNsec_ShouldWork()
    {
        // Arrange - Generate a key and get its nsec
        var (originalPrivateKeyHex, originalPublicKeyHex, originalNsec, originalNpub) = _nostrService.GenerateKeyPair();

        // Act - Import using the nsec
        var (importedPrivateKeyHex, importedPublicKeyHex, importedNsec, importedNpub) = _nostrService.ImportPrivateKey(originalNsec);

        // Assert - All values should match
        Assert.Equal(originalPrivateKeyHex, importedPrivateKeyHex);
        Assert.Equal(originalPublicKeyHex, importedPublicKeyHex);
        Assert.Equal(originalNsec, importedNsec);
        Assert.Equal(originalNpub, importedNpub);
    }

    [Fact]
    public void GenerateKeyPair_NsecRoundTrip_ShouldProduceSameKeys()
    {
        // This test verifies the complete flow: generate -> use nsec -> import
        // which is the exact flow that was broken before

        // Generate new identity
        var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.GenerateKeyPair();

        // Import using the generated nsec (simulates "Continue with this identity")
        var (importedPrivateKeyHex, importedPublicKeyHex, importedNsec, importedNpub) = _nostrService.ImportPrivateKey(nsec);

        // Everything should match
        Assert.Equal(privateKeyHex, importedPrivateKeyHex);
        Assert.Equal(publicKeyHex, importedPublicKeyHex);
        Assert.Equal(nsec, importedNsec);
        Assert.Equal(npub, importedNpub);
    }

    [Fact]
    public async Task ConnectAsync_ShouldUpdateConnectionStatus()
    {
        // Arrange
        var statusUpdates = new List<NostrConnectionStatus>();
        using var subscription = _nostrService.ConnectionStatus.Subscribe(status => statusUpdates.Add(status));

        // Act
        await _nostrService.ConnectAsync("wss://relay.test");

        // Assert
        Assert.Single(statusUpdates);
        Assert.Equal("wss://relay.test", statusUpdates[0].RelayUrl);
        Assert.True(statusUpdates[0].IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldUpdateConnectionStatus()
    {
        // Arrange
        await _nostrService.ConnectAsync("wss://relay.test");

        var statusUpdates = new List<NostrConnectionStatus>();
        using var subscription = _nostrService.ConnectionStatus.Subscribe(status => statusUpdates.Add(status));

        // Act
        await _nostrService.DisconnectAsync();

        // Assert
        Assert.Single(statusUpdates);
        Assert.False(statusUpdates[0].IsConnected);
    }

    [Fact]
    public void EncryptDecryptNip44_ShouldRoundTrip()
    {
        // Arrange
        var (senderPrivateKey, _, _, _) = _nostrService.GenerateKeyPair();
        var (recipientPrivateKey, recipientPublicKey, _, _) = _nostrService.GenerateKeyPair();
        var (_, senderPublicKey, _, _) = _nostrService.ImportPrivateKey(senderPrivateKey);

        var plaintext = "Hello, NIP-44!";

        // Act
        var encrypted = _nostrService.EncryptNip44(plaintext, senderPrivateKey, recipientPublicKey);
        var decrypted = _nostrService.DecryptNip44(encrypted, recipientPrivateKey, senderPublicKey);

        // Assert (currently using placeholder implementation)
        Assert.Equal(plaintext, decrypted);
    }
}
