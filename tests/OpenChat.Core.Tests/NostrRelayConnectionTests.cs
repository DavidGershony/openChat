using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class NostrRelayConnectionTests
{
    private static readonly ILogger<NostrRelayConnection> Logger = LoggingConfiguration.CreateLogger<NostrRelayConnection>();

    [Fact]
    public void Constructor_SetsRelayUrl()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        Assert.Equal("wss://relay.example.com", conn.RelayUrl);
    }

    [Fact]
    public void IsConnected_InitiallyFalse()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        Assert.False(conn.IsConnected);
    }

    [Fact]
    public void HasSubscription_ReturnsFalseForUnknownSubId()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        Assert.False(conn.HasSubscription("unknown_sub_id"));
    }

    [Fact]
    public void ActiveSubscriptions_InitiallyEmpty()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        Assert.Empty(conn.ActiveSubscriptions);
    }

    [Fact]
    public void CreateTemporary_SetsRelayUrl()
    {
        var conn = NostrRelayConnection.CreateTemporary("wss://relay.example.com", Logger);
        Assert.Equal("wss://relay.example.com", conn.RelayUrl);
        Assert.False(conn.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsOnInvalidUrl()
    {
        var conn = new NostrRelayConnection("wss://invalid.nonexistent.relay.test:9999", Logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Should throw because the relay doesn't exist
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ConnectAsync(cts.Token);
        });
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        await conn.DisposeAsync();
        await conn.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task SendAsync_ThrowsAfterDispose()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        await conn.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await conn.SendAsync(new byte[] { 0x01 });
        });
    }

    [Fact]
    public async Task ConnectAsync_ThrowsAfterDispose()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        await conn.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await conn.ConnectAsync();
        });
    }

    [Fact]
    public async Task RegisterSubscriptionAsync_TracksSubIdEvenWhenNotConnected()
    {
        // RegisterSubscriptionAsync stores the subscription even if send fails (not connected).
        // This allows HasSubscription to return true for H2 validation.
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);

        await conn.RegisterSubscriptionAsync("test_sub_1", "{\"kinds\":[1059]}");

        Assert.True(conn.HasSubscription("test_sub_1"));
        Assert.Single(conn.ActiveSubscriptions);
        Assert.Equal("{\"kinds\":[1059]}", conn.ActiveSubscriptions["test_sub_1"]);
    }

    [Fact]
    public async Task UnregisterSubscriptionAsync_RemovesTracking()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);

        await conn.RegisterSubscriptionAsync("test_sub_1", "{\"kinds\":[1059]}");
        Assert.True(conn.HasSubscription("test_sub_1"));

        await conn.UnregisterSubscriptionAsync("test_sub_1");
        Assert.False(conn.HasSubscription("test_sub_1"));
        Assert.Empty(conn.ActiveSubscriptions);
    }

    [Fact]
    public async Task RegisterMultipleSubscriptions_AllTracked()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);

        await conn.RegisterSubscriptionAsync("welcome_abc", "{\"kinds\":[1059]}");
        await conn.RegisterSubscriptionAsync("group_def", "{\"kinds\":[445]}");

        Assert.True(conn.HasSubscription("welcome_abc"));
        Assert.True(conn.HasSubscription("group_def"));
        Assert.False(conn.HasSubscription("unknown"));
        Assert.Equal(2, conn.ActiveSubscriptions.Count);
    }

    [Fact]
    public async Task RegisterSubscriptionAsync_OverwritesExistingFilter()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);

        await conn.RegisterSubscriptionAsync("sub_1", "{\"kinds\":[1059]}");
        await conn.RegisterSubscriptionAsync("sub_1", "{\"kinds\":[445]}");

        Assert.Single(conn.ActiveSubscriptions);
        Assert.Equal("{\"kinds\":[445]}", conn.ActiveSubscriptions["sub_1"]);
    }

    [Fact]
    public async Task UnregisterSubscriptionAsync_NoOpForUnknownSubId()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);

        // Should not throw
        await conn.UnregisterSubscriptionAsync("nonexistent_sub");
        Assert.Empty(conn.ActiveSubscriptions);
    }

    [Fact]
    public async Task DisposeAsync_ClearsSubscriptions()
    {
        var conn = new NostrRelayConnection("wss://relay.example.com", Logger);
        await conn.RegisterSubscriptionAsync("sub_1", "{\"kinds\":[1059]}");
        Assert.True(conn.HasSubscription("sub_1"));

        await conn.DisposeAsync();

        // After dispose, ActiveSubscriptions should be cleared
        Assert.Empty(conn.ActiveSubscriptions);
    }
}
