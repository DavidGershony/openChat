using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class TrackedSubscriptionTests
{
    [Fact]
    public void Constructor_SetsSubIdAndFilter()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var sub = new TrackedSubscription("sub1", filter);
        Assert.Equal("sub1", sub.SubId);
        Assert.Same(filter, sub.Filter);
    }

    [Fact]
    public void AddRelay_AppearsInActiveRelays()
    {
        var sub = new TrackedSubscription("sub1", ParsedFilter.FromJson("{}"));
        sub.AddRelay("wss://relay1.example.com");
        Assert.Contains("wss://relay1.example.com", sub.ActiveRelays);
    }

    [Fact]
    public void RemoveRelay_DisappearsFromActiveRelays()
    {
        var sub = new TrackedSubscription("sub1", ParsedFilter.FromJson("{}"));
        sub.AddRelay("wss://relay1.example.com");
        sub.RemoveRelay("wss://relay1.example.com");
        Assert.DoesNotContain("wss://relay1.example.com", sub.ActiveRelays);
    }

    [Fact]
    public void RemoveRelay_LastOne_ActiveRelaysEmpty()
    {
        var sub = new TrackedSubscription("sub1", ParsedFilter.FromJson("{}"));
        sub.AddRelay("wss://relay1.example.com");
        sub.RemoveRelay("wss://relay1.example.com");
        Assert.Empty(sub.ActiveRelays);
    }

    [Fact]
    public void HasRelay_ReturnsTrueForActive_FalseForUnknown()
    {
        var sub = new TrackedSubscription("sub1", ParsedFilter.FromJson("{}"));
        sub.AddRelay("wss://relay1.example.com");
        Assert.True(sub.HasRelay("wss://relay1.example.com"));
        Assert.False(sub.HasRelay("wss://unknown.example.com"));
    }

    [Fact]
    public void Filter_IsSameInstancePassedToConstructor()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[445],\"#h\":[\"g1\"]}");
        var sub = new TrackedSubscription("sub1", filter);
        Assert.Same(filter, sub.Filter);
    }
}
