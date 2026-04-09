using System.Text.Json;

namespace OpenChat.Core.Services;

/// <summary>
/// Single source of truth for a Nostr subscription filter (NIP-01).
/// Parsed once at registration, used for matching incoming events and
/// serialized back to JSON on reconnect.
/// </summary>
public class ParsedFilter
{
    public HashSet<int>? Kinds { get; private set; }
    public HashSet<string>? Authors { get; private set; }
    public HashSet<string>? Ids { get; private set; }
    public HashSet<string>? TagP { get; private set; }
    public HashSet<string>? TagE { get; private set; }
    public HashSet<string>? TagH { get; private set; }
    public long? Since { get; private set; }
    public long? Until { get; private set; }

    private ParsedFilter() { }

    /// <summary>
    /// Parse a Nostr filter from JSON (e.g. {"kinds":[1059],"#p":["abc123"]}).
    /// </summary>
    public static ParsedFilter FromJson(string filterJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(filterJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid filter JSON", ex);
        }

        var root = doc.RootElement;
        var filter = new ParsedFilter();

        if (root.TryGetProperty("kinds", out var kindsEl) && kindsEl.ValueKind == JsonValueKind.Array)
        {
            filter.Kinds = new HashSet<int>();
            foreach (var k in kindsEl.EnumerateArray())
                filter.Kinds.Add(k.GetInt32());
        }

        if (root.TryGetProperty("authors", out var authorsEl) && authorsEl.ValueKind == JsonValueKind.Array)
        {
            filter.Authors = new HashSet<string>();
            foreach (var a in authorsEl.EnumerateArray())
                filter.Authors.Add(a.GetString()!);
        }

        if (root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            filter.Ids = new HashSet<string>();
            foreach (var id in idsEl.EnumerateArray())
                filter.Ids.Add(id.GetString()!);
        }

        filter.TagP = ParseTagArray(root, "#p");
        filter.TagE = ParseTagArray(root, "#e");
        filter.TagH = ParseTagArray(root, "#h");

        if (root.TryGetProperty("since", out var sinceEl) && sinceEl.ValueKind == JsonValueKind.Number)
            filter.Since = sinceEl.GetInt64();

        if (root.TryGetProperty("until", out var untilEl) && untilEl.ValueKind == JsonValueKind.Number)
            filter.Until = untilEl.GetInt64();

        return filter;
    }

    /// <summary>
    /// Match an incoming event against this filter.
    /// Every present field must match (AND per NIP-01). Absent fields accept anything.
    /// </summary>
    public bool Matches(NostrEventReceived evt)
    {
        if (Kinds != null && !Kinds.Contains(evt.Kind))
            return false;

        if (Authors != null && !Authors.Contains(evt.PublicKey))
            return false;

        if (Ids != null && !Ids.Contains(evt.EventId))
            return false;

        if (TagP != null && !EventHasMatchingTag(evt, "p", TagP))
            return false;

        if (TagE != null && !EventHasMatchingTag(evt, "e", TagE))
            return false;

        if (TagH != null && !EventHasMatchingTag(evt, "h", TagH))
            return false;

        var unixTimestamp = new DateTimeOffset(evt.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds();

        if (Since.HasValue && unixTimestamp < Since.Value)
            return false;

        if (Until.HasValue && unixTimestamp > Until.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Serialize back to JSON for REQ replay on reconnect.
    /// </summary>
    public string ToFilterJson()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();

        if (Kinds != null)
        {
            writer.WritePropertyName("kinds");
            writer.WriteStartArray();
            foreach (var k in Kinds) writer.WriteNumberValue(k);
            writer.WriteEndArray();
        }

        if (Authors != null)
        {
            writer.WritePropertyName("authors");
            writer.WriteStartArray();
            foreach (var a in Authors) writer.WriteStringValue(a);
            writer.WriteEndArray();
        }

        if (Ids != null)
        {
            writer.WritePropertyName("ids");
            writer.WriteStartArray();
            foreach (var id in Ids) writer.WriteStringValue(id);
            writer.WriteEndArray();
        }

        WriteTagArray(writer, "#p", TagP);
        WriteTagArray(writer, "#e", TagE);
        WriteTagArray(writer, "#h", TagH);

        if (Since.HasValue)
            writer.WriteNumber("since", Since.Value);

        if (Until.HasValue)
            writer.WriteNumber("until", Until.Value);

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HashSet<string>? ParseTagArray(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>();
            foreach (var v in el.EnumerateArray())
                set.Add(v.GetString()!);
            return set;
        }
        return null;
    }

    private static bool EventHasMatchingTag(NostrEventReceived evt, string tagName, HashSet<string> values)
    {
        foreach (var tag in evt.Tags)
        {
            if (tag.Count >= 2 && tag[0] == tagName && values.Contains(tag[1]))
                return true;
        }
        return false;
    }

    private static void WriteTagArray(Utf8JsonWriter writer, string propertyName, HashSet<string>? values)
    {
        if (values == null) return;
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var v in values) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}
