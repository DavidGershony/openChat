using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace OpenChat.Diagnostics.WhitenoiseInterop;

/// <summary>
/// Drives the Whitenoise CLI inside a Docker container for interop testing.
/// Requires docker-compose.test.yml to be running with the 'whitenoise' service.
/// </summary>
public class WhitenoiseDockerClient : IAsyncDisposable
{
    private const string ContainerName = "whitenoise-interop";
    private const int DefaultTimeoutMs = 30_000;

    private readonly ITestOutputHelper _output;
    private string? _accountPubkeyHex;

    public string? PubkeyHex => _accountPubkeyHex;

    public WhitenoiseDockerClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask DisposeAsync()
    {
        if (_accountPubkeyHex != null)
        {
            try
            {
                await RunWnAsync("logout", _accountPubkeyHex);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Verify the whitenoise container is running and responsive.
    /// </summary>
    public async Task EnsureRunningAsync()
    {
        var result = await RunDockerAsync("inspect", "--format", "{{.State.Running}}", ContainerName);
        if (!result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Container '{ContainerName}' is not running. Start it with: docker compose -f docker-compose.test.yml up -d");
    }

    /// <summary>
    /// Create a new Nostr identity inside the Whitenoise daemon.
    /// Returns the pubkey hex string.
    /// </summary>
    public async Task<string> CreateIdentityAsync()
    {
        var json = await RunWnJsonAsync("create-identity");
        _output.WriteLine($"WN create-identity response: {json}");

        using var doc = JsonDocument.Parse(json);
        var result = UnwrapResult(doc);

        // result is the account object with "pubkey" field
        if (result.TryGetProperty("pubkey", out var pk))
        {
            _accountPubkeyHex = pk.GetString()!;
        }
        else
        {
            // Fallback: try whoami
            var whoami = await RunWnJsonAsync("whoami");
            using var whoamiDoc = JsonDocument.Parse(whoami);
            var whoamiResult = UnwrapResult(whoamiDoc);
            // whoami returns an array of accounts
            _accountPubkeyHex = whoamiResult.ValueKind == JsonValueKind.Array
                ? whoamiResult[0].GetProperty("pubkey").GetString()!
                : whoamiResult.GetProperty("pubkey").GetString()!;
        }

        _output.WriteLine($"WN identity created: {_accountPubkeyHex}");
        return _accountPubkeyHex;
    }

    /// <summary>
    /// Add a relay to the account's relay list so WN publishes/subscribes there.
    /// </summary>
    /// <summary>
    /// Add a relay to the account's relay list so WN publishes/subscribes there.
    /// Adds as all types (nip65, inbox, key_package) for full coverage.
    /// </summary>
    public async Task AddRelayAsync(string relayUrl)
    {
        foreach (var type in new[] { "nip65", "inbox", "key_package" })
        {
            var args = new List<string> { "relays", "add", "--type", type, relayUrl };
            if (_accountPubkeyHex != null)
                args.InsertRange(0, new[] { "--account", _accountPubkeyHex });
            try
            {
                var result = await RunWnAsync(args.ToArray());
                _output.WriteLine($"WN add relay ({type}): {result.Trim()}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"WN add relay ({type}) failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Create a group with the given name and member pubkeys.
    /// Returns the group ID hex.
    /// </summary>
    public async Task<string> CreateGroupAsync(string name, params string[] memberPubkeys)
    {
        var args = new List<string> { "groups", "create", name };
        args.AddRange(memberPubkeys);
        if (_accountPubkeyHex != null)
        {
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });
        }

        var json = await RunWnJsonAsync(args.ToArray());
        _output.WriteLine($"WN create-group response: {json}");

        using var doc = JsonDocument.Parse(json);
        var result = UnwrapResult(doc);
        var groupId = (result.TryGetProperty("group_id", out var gid) ? WhitenoiseGroup.ExtractGroupIdHex(gid)
                       : result.TryGetProperty("mls_group_id", out var gid2) ? WhitenoiseGroup.ExtractGroupIdHex(gid2)
                       : null) ?? throw new InvalidOperationException($"No group_id in response: {json}");
        return groupId;
    }

    /// <summary>
    /// List visible groups for the current account.
    /// </summary>
    public async Task<List<WhitenoiseGroup>> ListGroupsAsync()
    {
        var args = new List<string> { "groups", "list" };
        if (_accountPubkeyHex != null)
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });

        var json = await RunWnJsonAsync(args.ToArray());
        _output.WriteLine($"WN groups list: {json}");

        var groups = new List<WhitenoiseGroup>();
        using var doc = JsonDocument.Parse(json);
        var result = UnwrapResult(doc);

        var array = result.ValueKind == JsonValueKind.Array
            ? result
            : result.TryGetProperty("groups", out var g) ? g : result;

        foreach (var rawItem in array.EnumerateArray())
        {
            // visible_groups returns { "group": {...}, "membership": {...} }
            var item = rawItem.TryGetProperty("group", out var inner) ? inner : rawItem;

            groups.Add(new WhitenoiseGroup
            {
                GroupIdHex = item.TryGetProperty("group_id", out var gid) ? WhitenoiseGroup.ExtractGroupIdHex(gid)
                             : item.TryGetProperty("mls_group_id", out var gid2) ? WhitenoiseGroup.ExtractGroupIdHex(gid2)
                             : "",
                Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            });
        }

        return groups;
    }

    /// <summary>
    /// List pending group invites.
    /// </summary>
    public async Task<List<WhitenoiseGroup>> ListInvitesAsync()
    {
        var args = new List<string> { "groups", "invites" };
        if (_accountPubkeyHex != null)
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });

        var json = await RunWnJsonAsync(args.ToArray());
        _output.WriteLine($"WN invites: {json}");

        var groups = new List<WhitenoiseGroup>();
        using var doc = JsonDocument.Parse(json);
        var result = UnwrapResult(doc);

        var array = result.ValueKind == JsonValueKind.Array
            ? result
            : result.TryGetProperty("invites", out var inv) ? inv : result;

        foreach (var item in array.EnumerateArray())
        {
            groups.Add(new WhitenoiseGroup
            {
                GroupIdHex = item.TryGetProperty("group_id", out var gid) ? WhitenoiseGroup.ExtractGroupIdHex(gid)
                             : item.TryGetProperty("mls_group_id", out var gid2) ? WhitenoiseGroup.ExtractGroupIdHex(gid2)
                             : "",
                Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            });
        }

        return groups;
    }

    /// <summary>
    /// Accept a group invite by group ID.
    /// </summary>
    public async Task AcceptInviteAsync(string groupIdHex)
    {
        var args = new List<string> { "groups", "accept", groupIdHex };
        if (_accountPubkeyHex != null)
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });

        var result = await RunWnAsync(args.ToArray());
        _output.WriteLine($"WN accept invite: {result}");
    }

    /// <summary>
    /// Send a text message to a group.
    /// </summary>
    public async Task SendMessageAsync(string groupIdHex, string text)
    {
        var args = new List<string> { "messages", "send", groupIdHex, text };
        if (_accountPubkeyHex != null)
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });

        var result = await RunWnAsync(args.ToArray());
        _output.WriteLine($"WN send message: {result}");
    }

    /// <summary>
    /// Fetch messages in a group.
    /// </summary>
    public async Task<List<WhitenoiseMessage>> FetchMessagesAsync(string groupIdHex)
    {
        var args = new List<string> { "messages", "list", groupIdHex };
        if (_accountPubkeyHex != null)
            args.InsertRange(0, new[] { "--account", _accountPubkeyHex });

        var json = await RunWnJsonAsync(args.ToArray());
        _output.WriteLine($"WN messages: {json}");

        var messages = new List<WhitenoiseMessage>();
        using var doc = JsonDocument.Parse(json);
        var result = UnwrapResult(doc);

        var array = result.ValueKind == JsonValueKind.Array
            ? result
            : result.TryGetProperty("messages", out var msgs) ? msgs : result;

        foreach (var item in array.EnumerateArray())
        {
            messages.Add(new WhitenoiseMessage
            {
                SenderPubkeyHex = item.TryGetProperty("author", out var a) ? a.GetString() ?? ""
                                  : item.TryGetProperty("sender", out var s) ? s.GetString() ?? "" : "",
                Content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                Timestamp = item.TryGetProperty("created_at", out var t) ? t.GetUInt64() : 0,
            });
        }

        return messages;
    }

    /// <summary>
    /// Poll until a condition is met, with configurable timeout and interval.
    /// </summary>
    public async Task<T> PollUntilAsync<T>(
        Func<Task<T>> action,
        Func<T, bool> predicate,
        int timeoutMs = 30_000,
        int intervalMs = 1_000,
        string description = "condition")
    {
        var sw = Stopwatch.StartNew();
        T result = default!;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            result = await action();
            if (predicate(result))
            {
                _output.WriteLine($"WN poll '{description}' satisfied after {sw.ElapsedMilliseconds}ms");
                return result;
            }
            await Task.Delay(intervalMs);
        }
        _output.WriteLine($"WN poll '{description}' timed out after {timeoutMs}ms");
        return result;
    }

    // ── Docker exec helpers ──

    private async Task<string> RunWnJsonAsync(params string[] wnArgs)
    {
        var allArgs = new List<string>(wnArgs) { "--json" };
        return await RunWnAsync(allArgs.ToArray());
    }

    /// <summary>
    /// Unwrap the "result" property from WN CLI JSON responses.
    /// WN CLI wraps all output in { "result": ... } or { "error": ... }.
    /// </summary>
    private static JsonElement UnwrapResult(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("result", out var result))
            return result;
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"WN error: {error}");
        return doc.RootElement;
    }

    private async Task<string> RunWnAsync(params string[] wnArgs)
    {
        // Use bash -c to avoid MSYS/Git-bash path translation mangling Unix paths
        var wnCmd = "wn --socket /data/release/wnd.sock " + string.Join(" ", wnArgs.Select(EscapeArg));
        var dockerArgs = new List<string> { "exec", ContainerName, "bash", "-c", wnCmd };
        return await RunDockerAsync(dockerArgs.ToArray());
    }

    private static string EscapeArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\''))
            return "'" + arg.Replace("'", "'\\''") + "'";
        return arg;
    }

    private static async Task<string> RunDockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} failed (exit {process.ExitCode}):\n{stderr}\n{stdout}");

        return stdout;
    }
}

public class WhitenoiseGroup
{
    public string GroupIdHex { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Extract group ID hex from various WN CLI JSON formats.
    /// Handles: plain hex string, or { "value": { "vec": [byte, ...] } }
    /// </summary>
    public static string ExtractGroupIdHex(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";

        // Nested format: { "value": { "vec": [...] } }
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var value) &&
            value.TryGetProperty("vec", out var vec) &&
            vec.ValueKind == JsonValueKind.Array)
        {
            var bytes = new byte[vec.GetArrayLength()];
            int i = 0;
            foreach (var b in vec.EnumerateArray())
                bytes[i++] = (byte)b.GetInt32();
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        return "";
    }
}

public class WhitenoiseMessage
{
    public string SenderPubkeyHex { get; set; } = "";
    public string Content { get; set; } = "";
    public ulong Timestamp { get; set; }
}
