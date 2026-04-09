using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class NostrConnectionProviderTests : IAsyncDisposable
{
    private readonly NostrConnectionProvider _provider = new();

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    // --- Subscription tracking ---

    [Fact]
    public async Task SubscribeAsync_CreatesTrackedSubscription()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var subId = await _provider.SubscribeAsync("welcome", filter, Array.Empty<string>());

        var tracked = _provider.GetSubscription(subId);
        Assert.NotNull(tracked);
        Assert.Same(filter, tracked.Filter);
    }

    [Fact]
    public async Task SubscribeAsync_SubIdStartsWithPrefix()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var subId = await _provider.SubscribeAsync("welcome", filter, Array.Empty<string>());

        Assert.StartsWith("welcome_", subId);
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesSubscription()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var subId = await _provider.SubscribeAsync("welcome", filter, Array.Empty<string>());

        await _provider.UnsubscribeAsync(subId);

        Assert.Null(_provider.GetSubscription(subId));
    }

    [Fact]
    public void GetSubscription_ReturnsNullForUnknown()
    {
        Assert.Null(_provider.GetSubscription("nonexistent"));
    }

    // --- Firewall (the H2 fix) ---

    [Fact]
    public async Task ValidateEvent_MatchingEvent_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var subId = await _provider.SubscribeAsync("test", filter, Array.Empty<string>());

        var evt = MakeEvent(kind: 1059);
        Assert.True(_provider.ValidateEvent(subId, evt));
    }

    [Fact]
    public async Task ValidateEvent_WrongKind_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        var subId = await _provider.SubscribeAsync("test", filter, Array.Empty<string>());

        var evt = MakeEvent(kind: 445);
        Assert.False(_provider.ValidateEvent(subId, evt));
    }

    [Fact]
    public async Task ValidateEvent_WrongTagP_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059],\"#p\":[\"mypk\"]}");
        var subId = await _provider.SubscribeAsync("test", filter, Array.Empty<string>());

        var evt = MakeEvent(kind: 1059, tags: new List<List<string>> { new() { "p", "other" } });
        Assert.False(_provider.ValidateEvent(subId, evt));
    }

    [Fact]
    public void ValidateEvent_UnknownSubId_ReturnsFalse()
    {
        var evt = MakeEvent(kind: 1059);
        Assert.False(_provider.ValidateEvent("nonexistent", evt));
    }

    [Fact]
    public async Task ValidateEvent_EmptyFilter_AcceptsAll()
    {
        var filter = ParsedFilter.FromJson("{}");
        var subId = await _provider.SubscribeAsync("test", filter, Array.Empty<string>());

        var evt = MakeEvent(kind: 9999, pubkey: "anypk");
        Assert.True(_provider.ValidateEvent(subId, evt));
    }

    // --- Real filter patterns used in codebase ---

    [Fact]
    public async Task ValidateEvent_WelcomeFilter_AcceptsKind1059WithCorrectP()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059],\"#p\":[\"userpubkey\"]}");
        var subId = await _provider.SubscribeAsync("welcome", filter, Array.Empty<string>());

        // Accept: kind 1059 with matching p tag
        var good = MakeEvent(kind: 1059, tags: new List<List<string>> { new() { "p", "userpubkey" } });
        Assert.True(_provider.ValidateEvent(subId, good));

        // Reject: wrong kind
        var wrongKind = MakeEvent(kind: 445, tags: new List<List<string>> { new() { "p", "userpubkey" } });
        Assert.False(_provider.ValidateEvent(subId, wrongKind));

        // Reject: kind 1059 but wrong p tag
        var wrongP = MakeEvent(kind: 1059, tags: new List<List<string>> { new() { "p", "otherpk" } });
        Assert.False(_provider.ValidateEvent(subId, wrongP));
    }

    [Fact]
    public async Task ValidateEvent_GroupFilter_AcceptsKind445WithCorrectH()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[445],\"#h\":[\"group1\",\"group2\"]}");
        var subId = await _provider.SubscribeAsync("group", filter, Array.Empty<string>());

        // Accept: kind 445 with matching h tag
        var good = MakeEvent(kind: 445, tags: new List<List<string>> { new() { "h", "group1" } });
        Assert.True(_provider.ValidateEvent(subId, good));

        // Reject: wrong kind
        var wrongKind = MakeEvent(kind: 1059, tags: new List<List<string>> { new() { "h", "group1" } });
        Assert.False(_provider.ValidateEvent(subId, wrongKind));

        // Reject: kind 445 but unknown group
        var wrongH = MakeEvent(kind: 445, tags: new List<List<string>> { new() { "h", "group3" } });
        Assert.False(_provider.ValidateEvent(subId, wrongH));
    }

    [Fact]
    public async Task ValidateEvent_GroupFilterWithSince_RejectsOldEvents()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[445],\"#h\":[\"g1\"],\"since\":1700000000}");
        var subId = await _provider.SubscribeAsync("group", filter, Array.Empty<string>());

        // Reject: event too old
        var old = MakeEvent(kind: 445, createdAtUnix: 1699999999,
            tags: new List<List<string>> { new() { "h", "g1" } });
        Assert.False(_provider.ValidateEvent(subId, old));

        // Accept: event after since
        var fresh = MakeEvent(kind: 445, createdAtUnix: 1700000001,
            tags: new List<List<string>> { new() { "h", "g1" } });
        Assert.True(_provider.ValidateEvent(subId, fresh));
    }

    // --- Helpers ---

    private static NostrEventReceived MakeEvent(
        int kind = 1, string pubkey = "pk1", string eventId = "eid1",
        long createdAtUnix = 1700000000, List<List<string>>? tags = null)
    {
        return new NostrEventReceived
        {
            Kind = kind,
            PublicKey = pubkey,
            EventId = eventId,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAtUnix).UtcDateTime,
            Tags = tags ?? new List<List<string>>()
        };
    }
}
