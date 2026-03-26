using Moq;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class BlossomUploadServiceTests
{
    /// <summary>
    /// Verifies that BlossomUploadService throws when no private key AND no external signer are provided.
    /// The anonymous upload fails (no server), then BuildAuthHeaderAsync throws because there's no way to sign.
    /// </summary>
    [Fact]
    public async Task UploadAsync_WithNoPrivateKeyAndNoSigner_ThrowsInvalidOperation()
    {
        var service = new BlossomUploadService();
        // Point at a non-routable address so the HTTP call fails fast
        service.BlossomServerUrl = "http://192.0.2.1:1";
        var dummyData = new byte[] { 1, 2, 3 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadAsync(dummyData, privateKeyHex: null));

        Assert.Contains("no private key and no external signer", ex.Message);
    }

    /// <summary>
    /// Verifies that after calling SetExternalSigner with a connected signer,
    /// the upload service uses the signer for NIP-98 auth instead of throwing.
    /// Also verifies the anonymous upload is skipped for signer users.
    /// </summary>
    [Fact]
    public async Task UploadAsync_WithExternalSigner_UsesSignerForAuth()
    {
        var mockSigner = new Mock<IExternalSigner>();
        mockSigner.Setup(s => s.IsConnected).Returns(true);
        mockSigner.Setup(s => s.SignEventAsync(It.IsAny<UnsignedNostrEvent>()))
            .ReturnsAsync("{\"id\":\"abc\",\"pubkey\":\"def\",\"created_at\":0,\"kind\":24242,\"tags\":[],\"content\":\"\",\"sig\":\"ghi\"}");

        var service = new BlossomUploadService();
        service.SetExternalSigner(mockSigner.Object);
        // Point at a non-routable address so the HTTP call fails fast
        service.BlossomServerUrl = "http://192.0.2.1:1";
        var dummyData = new byte[] { 1, 2, 3 };

        // We expect a failure (no real server), but NOT the "no private key" error
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadAsync(dummyData, privateKeyHex: null));

        Assert.DoesNotContain("no private key and no external signer", ex.Message);

        // Verify the signer was actually called to sign the auth event
        mockSigner.Verify(s => s.SignEventAsync(It.Is<UnsignedNostrEvent>(e => e.Kind == 24242)), Times.Once);
    }

    /// <summary>
    /// Verifies that anonymous upload is skipped when external signer is connected and no private key.
    /// The signer path should go directly to authenticated upload.
    /// </summary>
    [Fact]
    public async Task UploadAsync_WithExternalSigner_SkipsAnonymousUpload()
    {
        var signCallCount = 0;
        var mockSigner = new Mock<IExternalSigner>();
        mockSigner.Setup(s => s.IsConnected).Returns(true);
        mockSigner.Setup(s => s.SignEventAsync(It.IsAny<UnsignedNostrEvent>()))
            .Callback(() => signCallCount++)
            .ReturnsAsync("{\"id\":\"abc\",\"pubkey\":\"def\",\"created_at\":0,\"kind\":24242,\"tags\":[],\"content\":\"\",\"sig\":\"ghi\"}");

        var service = new BlossomUploadService();
        service.SetExternalSigner(mockSigner.Object);
        service.BlossomServerUrl = "http://192.0.2.1:1";
        var dummyData = new byte[] { 1, 2, 3 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadAsync(dummyData, privateKeyHex: null));

        // Signer should be called exactly once (only the auth attempt, no anonymous attempt)
        Assert.Equal(1, signCallCount);
        // Error message should contain the connection error, not the "no private key" error
        Assert.Contains("Blossom upload failed:", ex.Message);
    }

    /// <summary>
    /// Verifies that the server error body is included in the exception message.
    /// </summary>
    [Fact]
    public async Task UploadAsync_PropagatesServerErrorDetails()
    {
        var service = new BlossomUploadService();
        // Point at a non-routable address — error will be a connection failure message
        service.BlossomServerUrl = "http://192.0.2.1:1";
        var dummyData = new byte[] { 1, 2, 3 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadAsync(dummyData, privateKeyHex: null));

        // Should NOT be a generic message — should contain detail about what went wrong
        Assert.DoesNotContain("both anonymous and authenticated", ex.Message);
    }

    /// <summary>
    /// Verifies that SetExternalSigner on the interface works (was previously missing from interface).
    /// </summary>
    [Fact]
    public void SetExternalSigner_IsAccessibleViaInterface()
    {
        IMediaUploadService service = new BlossomUploadService();
        var mockSigner = new Mock<IExternalSigner>();

        // Should compile and not throw — this was the missing interface method
        service.SetExternalSigner(mockSigner.Object);
    }
}
