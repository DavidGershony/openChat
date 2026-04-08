using System.Text.RegularExpressions;
using OpenChat.Core.Configuration;
using OpenChat.Core.Marmot;
using OpenChat.Core.Services;
using OpenChat.UI.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Security regression tests. Each test maps to a finding from the security audit (2026-04-08).
/// Naming: {FindingId}_{Description}
/// </summary>
public class SecurityTests
{
    #region C1 — Platform Launcher Hardening (tests use static validators, never Process.Start)

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://relay.damus.io")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void C1_ValidateUrl_AllowsHttps(string url)
    {
        var result = AvaloniaLauncher.ValidateUrl(url);
        Assert.NotNull(result);
        Assert.StartsWith("https://", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cmd.exe /c whoami")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void C1_ValidateUrl_BlocksDangerousInput(string url)
    {
        var result = AvaloniaLauncher.ValidateUrl(url);
        Assert.Null(result);
    }

    [Fact]
    public void C1_ValidateFolderPath_BlocksPathTraversal()
    {
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("C:\\Windows\\System32"));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("..\\..\\..\\Windows"));
    }

    [Fact]
    public void C1_ValidateFolderPath_AllowsAppDirectory()
    {
        var logDir = ProfileConfiguration.LogDirectory;
        var result = AvaloniaLauncher.ValidateFolderPath(logDir);
        Assert.NotNull(result);
    }

    [Fact]
    public void C1_ValidateUrl_BlocksEmptyAndWhitespace()
    {
        Assert.Null(AvaloniaLauncher.ValidateUrl(""));
        Assert.Null(AvaloniaLauncher.ValidateUrl("   "));
        Assert.Null(AvaloniaLauncher.ValidateUrl(null));
    }

    [Fact]
    public void C1_ValidateFolderPath_BlocksEmptyAndWhitespace()
    {
        Assert.Null(AvaloniaLauncher.ValidateFolderPath(""));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath("   "));
        Assert.Null(AvaloniaLauncher.ValidateFolderPath(null));
    }

    #endregion

    #region C2 — WSS-Only Relay Connections

    [Theory]
    [InlineData("wss://relay.example.com")]
    [InlineData("wss://relay.damus.io")]
    public async Task C2_WssRelay_Accepted(string url)
    {
        var result = await NostrService.ValidateRelayUrlAsync(url);
        // null = valid, or DNS error (acceptable for test domains)
        Assert.True(result == null || result.StartsWith("Cannot resolve hostname"),
            $"Expected null or DNS error, got: {result}");
    }

    [Theory]
    [InlineData("ws://relay.example.com")]
    [InlineData("ws://localhost:7777")]
    public async Task C2_WsRelay_RejectedByDefault(string url)
    {
        // Ensure AllowLocalRelays is off for this test
        var previousValue = ProfileConfiguration.AllowLocalRelays;
        ProfileConfiguration.SetAllowLocalRelays(false);
        try
        {
            var result = await NostrService.ValidateRelayUrlAsync(url);
            Assert.NotNull(result);
            Assert.Contains("wss://", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProfileConfiguration.SetAllowLocalRelays(previousValue);
        }
    }

    [Fact]
    public async Task C2_WsRelay_AllowedWithLocalRelaysFlag()
    {
        ProfileConfiguration.SetAllowLocalRelays(true);
        try
        {
            var result = await NostrService.ValidateRelayUrlAsync("ws://relay.example.com");
            // Should be accepted (or DNS error, not scheme error)
            Assert.True(result == null || result.StartsWith("Cannot resolve hostname"),
                $"Expected null or DNS error, got: {result}");
        }
        finally
        {
            ProfileConfiguration.SetAllowLocalRelays(false);
        }
    }

    #endregion

    #region C3 — Event Timestamp Validation (Replay Prevention)

    [Fact]
    public void C3_FreshTimestamp_Accepted()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(now, isGiftWrap: false));
    }

    [Fact]
    public void C3_OldTimestamp_Rejected()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();
        Assert.False(NostrService.IsTimestampAcceptable(old, isGiftWrap: false));
    }

    [Fact]
    public void C3_FutureTimestamp_Rejected()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        Assert.False(NostrService.IsTimestampAcceptable(future, isGiftWrap: false));
    }

    [Fact]
    public void C3_GiftWrapOldTimestamp_Accepted()
    {
        // Gift wraps (kind 1059) use randomized timestamps, so old timestamps should be accepted
        var old = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(old, isGiftWrap: true));
    }

    [Fact]
    public void C3_ReplaceableEventOldTimestamp_Accepted()
    {
        // Kind 0 (metadata), 3 (contacts), 10002 (relay list) are replaceable — historical is fine
        var old = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        Assert.True(NostrService.IsTimestampAcceptable(old, isReplaceableEvent: true));
    }

    #endregion

    #region H1 — MediaCacheService Path Traversal

    [Theory]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void H1_ValidMessageId_Accepted(string messageId)
    {
        Assert.True(MediaCacheService.IsValidMessageId(messageId));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ZZZZZZ0123456789abcdef0123456789abcdef0123456789abcdef0123456789")] // non-hex chars
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")] // 63 chars
    public void H1_InvalidMessageId_Rejected(string messageId)
    {
        Assert.False(MediaCacheService.IsValidMessageId(messageId));
    }

    #endregion

    #region H3 — Native Interop Bounds Checking

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(50_000_000)]
    public void H3_ValidBufferLength_Accepted(int length)
    {
        var ex = Record.Exception(() => MarmotWrapper.ValidateNativeBufferLength(length, "test"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void H3_NegativeBufferLength_Rejected(int length)
    {
        Assert.Throws<MarmotException>(() => MarmotWrapper.ValidateNativeBufferLength(length, "test"));
    }

    [Fact]
    public void H3_OversizedBufferLength_Rejected()
    {
        Assert.Throws<MarmotException>(() =>
            MarmotWrapper.ValidateNativeBufferLength(200_000_000, "test"));
    }

    #endregion
}
