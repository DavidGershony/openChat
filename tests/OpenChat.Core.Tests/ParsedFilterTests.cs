using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

public class ParsedFilterTests
{
    // --- Parsing from JSON ---

    [Fact]
    public void Parse_KindsOnly_ExtractsKinds()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        Assert.NotNull(filter.Kinds);
        Assert.Single(filter.Kinds);
        Assert.Contains(1059, filter.Kinds);
    }

    [Fact]
    public void Parse_MultipleKinds_ExtractsAll()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059,445,0]}");
        Assert.Equal(3, filter.Kinds!.Count);
        Assert.Contains(1059, filter.Kinds);
        Assert.Contains(445, filter.Kinds);
        Assert.Contains(0, filter.Kinds);
    }

    [Fact]
    public void Parse_TagP_ExtractsPubkeys()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059],\"#p\":[\"abc\",\"def\"]}");
        Assert.NotNull(filter.TagP);
        Assert.Equal(2, filter.TagP.Count);
        Assert.Contains("abc", filter.TagP);
        Assert.Contains("def", filter.TagP);
    }

    [Fact]
    public void Parse_TagH_ExtractsGroupIds()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[445],\"#h\":[\"g1\",\"g2\"]}");
        Assert.NotNull(filter.TagH);
        Assert.Equal(2, filter.TagH.Count);
        Assert.Contains("g1", filter.TagH);
    }

    [Fact]
    public void Parse_TagE_ExtractsEventIds()
    {
        var filter = ParsedFilter.FromJson("{\"#e\":[\"eid1\"]}");
        Assert.NotNull(filter.TagE);
        Assert.Single(filter.TagE);
        Assert.Contains("eid1", filter.TagE);
    }

    [Fact]
    public void Parse_Authors_ExtractsAuthors()
    {
        var filter = ParsedFilter.FromJson("{\"authors\":[\"pk1\",\"pk2\"]}");
        Assert.NotNull(filter.Authors);
        Assert.Equal(2, filter.Authors.Count);
        Assert.Contains("pk1", filter.Authors);
    }

    [Fact]
    public void Parse_Ids_ExtractsIds()
    {
        var filter = ParsedFilter.FromJson("{\"ids\":[\"id1\",\"id2\"]}");
        Assert.NotNull(filter.Ids);
        Assert.Equal(2, filter.Ids.Count);
        Assert.Contains("id1", filter.Ids);
    }

    [Fact]
    public void Parse_Since_ExtractsTimestamp()
    {
        var filter = ParsedFilter.FromJson("{\"since\":1700000000}");
        Assert.Equal(1700000000L, filter.Since);
    }

    [Fact]
    public void Parse_Until_ExtractsTimestamp()
    {
        var filter = ParsedFilter.FromJson("{\"until\":1800000000}");
        Assert.Equal(1800000000L, filter.Until);
    }

    [Fact]
    public void Parse_EmptyFilter_AllFieldsNull()
    {
        var filter = ParsedFilter.FromJson("{}");
        Assert.Null(filter.Kinds);
        Assert.Null(filter.Authors);
        Assert.Null(filter.Ids);
        Assert.Null(filter.TagP);
        Assert.Null(filter.TagE);
        Assert.Null(filter.TagH);
        Assert.Null(filter.Since);
        Assert.Null(filter.Until);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<ArgumentException>(() => ParsedFilter.FromJson("not json"));
    }

    // --- Matching events ---

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

    [Fact]
    public void Matches_CorrectKind_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        Assert.True(filter.Matches(MakeEvent(kind: 1059)));
    }

    [Fact]
    public void Matches_WrongKind_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059]}");
        Assert.False(filter.Matches(MakeEvent(kind: 445)));
    }

    [Fact]
    public void Matches_NoKindsInFilter_AcceptsAnyKind()
    {
        var filter = ParsedFilter.FromJson("{}");
        Assert.True(filter.Matches(MakeEvent(kind: 9999)));
    }

    [Fact]
    public void Matches_CorrectAuthor_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"authors\":[\"abc\"]}");
        Assert.True(filter.Matches(MakeEvent(pubkey: "abc")));
    }

    [Fact]
    public void Matches_WrongAuthor_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"authors\":[\"abc\"]}");
        Assert.False(filter.Matches(MakeEvent(pubkey: "wrong")));
    }

    [Fact]
    public void Matches_CorrectTagP_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"#p\":[\"mypk\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "p", "mypk" } });
        Assert.True(filter.Matches(evt));
    }

    [Fact]
    public void Matches_WrongTagP_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"#p\":[\"mypk\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "p", "other" } });
        Assert.False(filter.Matches(evt));
    }

    [Fact]
    public void Matches_MissingTagP_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"#p\":[\"mypk\"]}");
        var evt = MakeEvent(tags: new List<List<string>>());
        Assert.False(filter.Matches(evt));
    }

    [Fact]
    public void Matches_CorrectTagH_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"#h\":[\"g1\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "h", "g1" } });
        Assert.True(filter.Matches(evt));
    }

    [Fact]
    public void Matches_WrongTagH_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"#h\":[\"g1\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "h", "g2" } });
        Assert.False(filter.Matches(evt));
    }

    [Fact]
    public void Matches_CorrectTagE_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"#e\":[\"eid1\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "e", "eid1" } });
        Assert.True(filter.Matches(evt));
    }

    [Fact]
    public void Matches_CorrectId_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"ids\":[\"abc\"]}");
        Assert.True(filter.Matches(MakeEvent(eventId: "abc")));
    }

    [Fact]
    public void Matches_WrongId_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"ids\":[\"abc\"]}");
        Assert.False(filter.Matches(MakeEvent(eventId: "wrong")));
    }

    [Fact]
    public void Matches_EventBeforeSince_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"since\":1700000000}");
        Assert.False(filter.Matches(MakeEvent(createdAtUnix: 1699999999)));
    }

    [Fact]
    public void Matches_EventAfterSince_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"since\":1700000000}");
        Assert.True(filter.Matches(MakeEvent(createdAtUnix: 1700000001)));
    }

    [Fact]
    public void Matches_EventAfterUntil_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"until\":1800000000}");
        Assert.False(filter.Matches(MakeEvent(createdAtUnix: 1800000001)));
    }

    [Fact]
    public void Matches_AllCriteriaMustMatch_KindOk_AuthorWrong_ReturnsFalse()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059],\"authors\":[\"abc\"]}");
        Assert.False(filter.Matches(MakeEvent(kind: 1059, pubkey: "wrong")));
    }

    [Fact]
    public void Matches_AllCriteriaMustMatch_AllOk_ReturnsTrue()
    {
        var filter = ParsedFilter.FromJson("{\"kinds\":[1059],\"authors\":[\"abc\"],\"#p\":[\"target\"]}");
        var evt = MakeEvent(kind: 1059, pubkey: "abc",
            tags: new List<List<string>> { new() { "p", "target" } });
        Assert.True(filter.Matches(evt));
    }

    [Fact]
    public void Matches_MultipleTagValues_AnyMatchSuffices()
    {
        var filter = ParsedFilter.FromJson("{\"#h\":[\"g1\",\"g2\"]}");
        var evt = MakeEvent(tags: new List<List<string>> { new() { "h", "g2" } });
        Assert.True(filter.Matches(evt));
    }

    // --- Serialization round-trip ---

    [Fact]
    public void ToFilterJson_RoundTrips_KindsAndTagP()
    {
        var original = ParsedFilter.FromJson("{\"kinds\":[1059],\"#p\":[\"abc\"]}");
        var json = original.ToFilterJson();
        var reparsed = ParsedFilter.FromJson(json);
        Assert.Equal(original.Kinds, reparsed.Kinds);
        Assert.Equal(original.TagP, reparsed.TagP);
    }

    [Fact]
    public void ToFilterJson_RoundTrips_KindsAndTagH_WithSince()
    {
        var original = ParsedFilter.FromJson("{\"kinds\":[445],\"#h\":[\"g1\"],\"since\":1700000000}");
        var json = original.ToFilterJson();
        var reparsed = ParsedFilter.FromJson(json);
        Assert.Equal(original.Kinds, reparsed.Kinds);
        Assert.Equal(original.TagH, reparsed.TagH);
        Assert.Equal(original.Since, reparsed.Since);
    }

    [Fact]
    public void ToFilterJson_EmptyFilter_ProducesEmptyObject()
    {
        var filter = ParsedFilter.FromJson("{}");
        Assert.Equal("{}", filter.ToFilterJson());
    }
}
