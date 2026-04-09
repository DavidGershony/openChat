using Moq;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class BridgeAuthServiceTests
{
    private readonly Mock<IStorageService> _mockStorage;
    private readonly BridgeAuthService _authService;

    public BridgeAuthServiceTests()
    {
        _mockStorage = new Mock<IStorageService>();
        _mockStorage.Setup(x => x.SaveSettingAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockStorage.Setup(x => x.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        _authService = new BridgeAuthService(_mockStorage.Object);
    }

    [Fact]
    public void GeneratePairingCode_Returns6DigitCode()
    {
        var code = _authService.GeneratePairingCode();

        Assert.NotNull(code);
        Assert.Equal(6, code.Length);
        Assert.True(int.TryParse(code, out var num));
        Assert.InRange(num, 0, 999999);
    }

    [Fact]
    public void GeneratePairingCode_ReturnsDifferentCodesOnSubsequentCalls()
    {
        // Generate many codes and verify they're not all the same
        var codes = Enumerable.Range(0, 10).Select(_ => _authService.GeneratePairingCode()).ToHashSet();

        // With 6-digit codes, 10 calls should produce at least 2 distinct values
        Assert.True(codes.Count > 1, "Expected different codes across multiple generations");
    }

    [Fact]
    public async Task TryPairAsync_WithCorrectCode_ReturnsToken()
    {
        var code = _authService.GeneratePairingCode();

        var token = await _authService.TryPairAsync(code);

        Assert.NotNull(token);
        Assert.NotEmpty(token);
    }

    [Fact]
    public async Task TryPairAsync_WithCorrectCode_PersistsToken()
    {
        var code = _authService.GeneratePairingCode();

        await _authService.TryPairAsync(code);

        _mockStorage.Verify(x => x.SaveSettingAsync("bridge_auth_token", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TryPairAsync_WithWrongCode_ReturnsNull()
    {
        _authService.GeneratePairingCode();

        var token = await _authService.TryPairAsync("000000");

        // May or may not be null depending on if the generated code happened to be 000000
        // Use a definitely wrong code format
        var code = _authService.GeneratePairingCode();
        var wrongCode = code == "123456" ? "654321" : "123456";
        var result = await _authService.TryPairAsync(wrongCode);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryPairAsync_WithNoPendingCode_ReturnsNull()
    {
        // Never called GeneratePairingCode
        var token = await _authService.TryPairAsync("123456");

        Assert.Null(token);
    }

    [Fact]
    public async Task TryPairAsync_CodeCanOnlyBeUsedOnce()
    {
        var code = _authService.GeneratePairingCode();

        var firstAttempt = await _authService.TryPairAsync(code);
        var secondAttempt = await _authService.TryPairAsync(code);

        Assert.NotNull(firstAttempt);
        Assert.Null(secondAttempt);
    }

    [Fact]
    public async Task ValidateToken_WithCorrectToken_ReturnsTrue()
    {
        var code = _authService.GeneratePairingCode();
        var token = await _authService.TryPairAsync(code);

        Assert.NotNull(token);
        Assert.True(_authService.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_WithNoActiveToken_ReturnsFalse()
    {
        Assert.False(_authService.ValidateToken("some-token"));
    }

    [Fact]
    public async Task ValidateToken_WithWrongToken_ReturnsFalse()
    {
        var code = _authService.GeneratePairingCode();
        await _authService.TryPairAsync(code);

        Assert.False(_authService.ValidateToken("wrong-token"));
    }

    [Fact]
    public async Task UnpairAsync_InvalidatesToken()
    {
        var code = _authService.GeneratePairingCode();
        var token = await _authService.TryPairAsync(code);
        Assert.NotNull(token);
        Assert.True(_authService.HasActiveToken);

        await _authService.UnpairAsync();

        Assert.False(_authService.HasActiveToken);
        Assert.False(_authService.ValidateToken(token));
    }

    [Fact]
    public async Task UnpairAsync_ClearsPersistedToken()
    {
        var code = _authService.GeneratePairingCode();
        await _authService.TryPairAsync(code);

        await _authService.UnpairAsync();

        _mockStorage.Verify(x => x.SaveSettingAsync("bridge_auth_token", ""), Times.Once);
    }

    [Fact]
    public async Task LoadPersistedTokenAsync_WithSavedToken_RestoresIt()
    {
        _mockStorage.Setup(x => x.GetSettingAsync("bridge_auth_token"))
            .ReturnsAsync("persisted-token");

        await _authService.LoadPersistedTokenAsync();

        Assert.True(_authService.HasActiveToken);
        Assert.True(_authService.ValidateToken("persisted-token"));
    }

    [Fact]
    public async Task LoadPersistedTokenAsync_WithNoSavedToken_StaysUnpaired()
    {
        _mockStorage.Setup(x => x.GetSettingAsync("bridge_auth_token"))
            .ReturnsAsync((string?)null);

        await _authService.LoadPersistedTokenAsync();

        Assert.False(_authService.HasActiveToken);
    }

    [Fact]
    public async Task LoadPersistedTokenAsync_WithEmptyToken_StaysUnpaired()
    {
        _mockStorage.Setup(x => x.GetSettingAsync("bridge_auth_token"))
            .ReturnsAsync("");

        await _authService.LoadPersistedTokenAsync();

        Assert.False(_authService.HasActiveToken);
    }

    [Fact]
    public void HasActiveToken_InitiallyFalse()
    {
        Assert.False(_authService.HasActiveToken);
    }

    [Fact]
    public async Task HasActiveToken_TrueAfterPairing()
    {
        var code = _authService.GeneratePairingCode();
        await _authService.TryPairAsync(code);

        Assert.True(_authService.HasActiveToken);
    }

    // ExtractBearerToken tests

    [Fact]
    public void ExtractBearerToken_WithValidHeader_ReturnsToken()
    {
        var token = BridgeAuthService.ExtractBearerToken("Bearer abc123");
        Assert.Equal("abc123", token);
    }

    [Fact]
    public void ExtractBearerToken_CaseInsensitivePrefix()
    {
        var token = BridgeAuthService.ExtractBearerToken("bearer abc123");
        Assert.Equal("abc123", token);
    }

    [Fact]
    public void ExtractBearerToken_WithNull_ReturnsNull()
    {
        Assert.Null(BridgeAuthService.ExtractBearerToken(null));
    }

    [Fact]
    public void ExtractBearerToken_WithEmpty_ReturnsNull()
    {
        Assert.Null(BridgeAuthService.ExtractBearerToken(""));
    }

    [Fact]
    public void ExtractBearerToken_WithWrongScheme_ReturnsNull()
    {
        Assert.Null(BridgeAuthService.ExtractBearerToken("Basic abc123"));
    }

    [Fact]
    public void ExtractBearerToken_TrimsWhitespace()
    {
        var token = BridgeAuthService.ExtractBearerToken("Bearer  abc123  ");
        Assert.Equal("abc123", token);
    }

    [Fact]
    public async Task NewPairingCode_OverridesPrevious()
    {
        var code1 = _authService.GeneratePairingCode();
        var code2 = _authService.GeneratePairingCode();

        // The first code should no longer work
        var result1 = await _authService.TryPairAsync(code1);

        // Only the second code should work (if they differ)
        if (code1 != code2)
        {
            Assert.Null(result1);
        }
    }

    [Fact]
    public async Task GeneratedTokens_AreDifferentEachTime()
    {
        var code1 = _authService.GeneratePairingCode();
        var token1 = await _authService.TryPairAsync(code1);

        // Unpair and pair again
        await _authService.UnpairAsync();
        var code2 = _authService.GeneratePairingCode();
        var token2 = await _authService.TryPairAsync(code2);

        Assert.NotNull(token1);
        Assert.NotNull(token2);
        // Tokens should differ due to random nonce
        Assert.NotEqual(token1, token2);
    }
}
