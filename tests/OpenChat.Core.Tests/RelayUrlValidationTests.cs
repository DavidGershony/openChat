using System.Net;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Proves that relay URL validation blocks SSRF attacks (private IPs, wrong schemes).
/// </summary>
public class RelayUrlValidationTests
{
    #region Scheme validation

    [Theory]
    [InlineData("wss://relay.example.com")]
    [InlineData("ws://relay.example.com")]
    [InlineData("wss://relay.damus.io")]
    public async Task ValidRelayUrl_Accepted(string url)
    {
        var result = await NostrService.ValidateRelayUrlAsync(url);
        // null or DNS resolution failure — both acceptable for valid scheme+format
        Assert.True(result == null || result.StartsWith("Cannot resolve hostname"),
            $"Expected null or DNS error, got: {result}");
    }

    [Theory]
    [InlineData("http://relay.example.com")]
    [InlineData("https://relay.example.com")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("file:///etc/passwd")]
    public async Task NonWebSocketScheme_Rejected(string url)
    {
        var result = await NostrService.ValidateRelayUrlAsync(url);
        Assert.NotNull(result);
        Assert.Contains("ws://", result);
    }

    [Fact]
    public async Task InvalidUrl_Rejected()
    {
        var result = await NostrService.ValidateRelayUrlAsync("not-a-url");
        Assert.NotNull(result);
        Assert.Contains("Invalid URL", result);
    }

    #endregion

    #region Private IP blocking

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.1.100")]
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    public void PrivateIpv4_IsBlocked(string ip)
    {
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]    // Just outside 172.16-31.x
    [InlineData("172.15.255.255")] // Just below 172.16.x
    [InlineData("11.0.0.1")]      // Just outside 10.x
    public void PublicIpv4_IsAllowed(string ip)
    {
        Assert.False(NostrService.IsPrivateOrReservedIp(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Ipv6Loopback_IsBlocked()
    {
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.IPv6Loopback)); // ::1
    }

    [Fact]
    public void Ipv6UniqueLocal_IsBlocked()
    {
        // fc00::/7 — unique local address
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.Parse("fc00::1")));
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void Ipv6LinkLocal_IsBlocked()
    {
        // fe80::/10
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.Parse("fe80::1")));
    }

    [Fact]
    public void Ipv6Unspecified_IsBlocked()
    {
        Assert.True(NostrService.IsPrivateOrReservedIp(IPAddress.IPv6Any)); // ::
    }

    [Fact]
    public void Ipv6PublicAddress_IsAllowed()
    {
        // 2606:4700:4700::1111 (Cloudflare DNS)
        Assert.False(NostrService.IsPrivateOrReservedIp(IPAddress.Parse("2606:4700:4700::1111")));
    }

    #endregion

    #region Full URL validation with private IPs

    [Fact]
    public async Task RelayUrl_Localhost_Rejected()
    {
        var result = await NostrService.ValidateRelayUrlAsync("ws://localhost:7777");
        Assert.NotNull(result);
        Assert.Contains("private", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelayUrl_PrivateIp_Rejected()
    {
        var result = await NostrService.ValidateRelayUrlAsync("wss://192.168.1.1:443");
        Assert.NotNull(result);
        Assert.Contains("private", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelayUrl_LoopbackIp_Rejected()
    {
        var result = await NostrService.ValidateRelayUrlAsync("ws://127.0.0.1:8080");
        Assert.NotNull(result);
        Assert.Contains("private", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
