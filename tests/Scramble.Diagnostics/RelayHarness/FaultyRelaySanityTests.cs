using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Scramble.Diagnostics.RelayHarness;

/// <summary>
/// Smoke-tests the FaultyRelay shim itself, with no Scramble code in the loop.
/// If these fail, the shim is broken; if they pass, the shim's behavior under
/// each fault knob is what later tests assume.
/// </summary>
[Trait("Category", "RelayHarness")]
public class FaultyRelaySanityTests
{
    private readonly ITestOutputHelper _output;

    public FaultyRelaySanityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PublishedEvent_GetsOk_AndIsRetrievableViaSubscription()
    {
        await using var relay = new FaultyRelay();
        await relay.StartAsync();
        _output.WriteLine($"relay listening at {relay.WsUrl}");

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);

        var evt = MakeEvent("evt-001", kind: 1, content: "hello");
        await SendAsync(ws, $"[\"EVENT\",{evt}]");

        var ok = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        Assert.NotNull(ok);
        _output.WriteLine($"got: {ok}");

        using var okDoc = JsonDocument.Parse(ok!);
        Assert.Equal("OK", okDoc.RootElement[0].GetString());
        Assert.Equal("evt-001", okDoc.RootElement[1].GetString());
        Assert.True(okDoc.RootElement[2].GetBoolean());

        // Now subscribe and confirm the stored event comes back.
        await SendAsync(ws, "[\"REQ\",\"sub-001\",{\"kinds\":[1]}]");

        var evtBack = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        Assert.NotNull(evtBack);
        _output.WriteLine($"got: {evtBack}");
        using var evtDoc = JsonDocument.Parse(evtBack!);
        Assert.Equal("EVENT", evtDoc.RootElement[0].GetString());
        Assert.Equal("sub-001", evtDoc.RootElement[1].GetString());
        Assert.Equal("evt-001", evtDoc.RootElement[2].GetProperty("id").GetString());

        var eose = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        using var eoseDoc = JsonDocument.Parse(eose!);
        Assert.Equal("EOSE", eoseDoc.RootElement[0].GetString());
    }

    [Fact]
    public async Task DropOk_StoresAndBroadcastsEvent_ButPublisherSeesNoOk()
    {
        await using var relay = new FaultyRelay();
        await relay.StartAsync();

        // First connect a subscriber on a separate socket so we can prove the
        // event is broadcast even when OK is dropped.
        using var subWs = new ClientWebSocket();
        await subWs.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);
        await SendAsync(subWs, "[\"REQ\",\"sub-A\",{\"kinds\":[1]}]");

        // Drain the EOSE for the empty initial result.
        var initialEose = await ReceiveAsync(subWs, TimeSpan.FromSeconds(2));
        using (var d = JsonDocument.Parse(initialEose!))
            Assert.Equal("EOSE", d.RootElement[0].GetString());

        // Flip the fault knob — from now on, EVENTs get stored + broadcast but no OK is sent.
        relay.Faults.DropOk = true;

        // Publisher socket.
        using var pubWs = new ClientWebSocket();
        await pubWs.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);

        var evt = MakeEvent("evt-002", kind: 1, content: "dropped-ok");
        await SendAsync(pubWs, $"[\"EVENT\",{evt}]");

        // Subscriber MUST receive it (this is the self-echo path the bug depends on).
        var evtBack = await ReceiveAsync(subWs, TimeSpan.FromSeconds(2));
        Assert.NotNull(evtBack);
        using (var d = JsonDocument.Parse(evtBack!))
        {
            Assert.Equal("EVENT", d.RootElement[0].GetString());
            Assert.Equal("evt-002", d.RootElement[2].GetProperty("id").GetString());
        }

        // Publisher MUST NOT receive an OK within a generous window.
        var publisherReply = await ReceiveAsync(pubWs, TimeSpan.FromMilliseconds(750));
        Assert.Null(publisherReply);

        // And the event was actually stored.
        Assert.Equal(1, relay.StoredEventCount);
    }

    [Fact]
    public async Task RejectAllEvents_SendsOkFalse_AndDoesNotStore()
    {
        await using var relay = new FaultyRelay();
        await relay.StartAsync();
        relay.Faults.RejectAllEvents = true;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);

        var evt = MakeEvent("evt-003", kind: 1, content: "rejected");
        await SendAsync(ws, $"[\"EVENT\",{evt}]");

        var reply = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        Assert.NotNull(reply);
        using var doc = JsonDocument.Parse(reply!);
        Assert.Equal("OK", doc.RootElement[0].GetString());
        Assert.False(doc.RootElement[2].GetBoolean());

        Assert.Equal(0, relay.StoredEventCount);
    }

    [Fact]
    public async Task DelayOkMs_DelaysOk_ButEventStoredImmediately()
    {
        await using var relay = new FaultyRelay();
        await relay.StartAsync();
        relay.Faults.DelayOkMs = 400;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);

        var evt = MakeEvent("evt-004", kind: 1, content: "delayed");
        var start = DateTime.UtcNow;
        await SendAsync(ws, $"[\"EVENT\",{evt}]");

        var reply = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        var elapsed = DateTime.UtcNow - start;
        Assert.NotNull(reply);
        Assert.True(elapsed.TotalMilliseconds >= 350,
            $"expected OK delayed by at least 350ms, got {elapsed.TotalMilliseconds}ms");

        using var doc = JsonDocument.Parse(reply!);
        Assert.Equal("OK", doc.RootElement[0].GetString());
        Assert.True(doc.RootElement[2].GetBoolean());
        Assert.Equal(1, relay.StoredEventCount);
    }

    [Fact]
    public async Task TagFilter_OnlyMatchesEventsWithTag()
    {
        await using var relay = new FaultyRelay();
        await relay.StartAsync();

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(relay.WsUrl), CancellationToken.None);

        // Publish two events: one with #h tag matching, one without.
        var match = MakeEvent("evt-h-1", kind: 445, tags: "[[\"h\",\"groupX\"]]");
        var noMatch = MakeEvent("evt-h-2", kind: 445, tags: "[[\"h\",\"groupY\"]]");
        await SendAsync(ws, $"[\"EVENT\",{match}]");
        await ReceiveAsync(ws, TimeSpan.FromSeconds(2)); // ok
        await SendAsync(ws, $"[\"EVENT\",{noMatch}]");
        await ReceiveAsync(ws, TimeSpan.FromSeconds(2)); // ok

        await SendAsync(ws, "[\"REQ\",\"sub-h\",{\"kinds\":[445],\"#h\":[\"groupX\"]}]");

        var first = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        using var firstDoc = JsonDocument.Parse(first!);
        Assert.Equal("EVENT", firstDoc.RootElement[0].GetString());
        Assert.Equal("evt-h-1", firstDoc.RootElement[2].GetProperty("id").GetString());

        // Should be EOSE next, not another EVENT.
        var second = await ReceiveAsync(ws, TimeSpan.FromSeconds(2));
        using var secondDoc = JsonDocument.Parse(second!);
        Assert.Equal("EOSE", secondDoc.RootElement[0].GetString());
    }

    // ── Helpers ──

    private static string MakeEvent(string id, int kind, string content = "", string tags = "[]")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{{\"id\":\"{id}\",\"pubkey\":\"00\",\"created_at\":{ts},\"kind\":{kind},\"tags\":{tags},\"content\":{JsonSerializer.Serialize(content)},\"sig\":\"00\"}}";
    }

    private static async Task SendAsync(ClientWebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
    }
}
