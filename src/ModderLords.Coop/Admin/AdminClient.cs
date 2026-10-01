using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ModderLords.Coop.Admin;

/// <summary>One <c>@ML@{...}</c> reply from the server's admin commands (the module's AdminWire).</summary>
public sealed record AdminReply(string Event, string RequestId, bool Ok, string? Error, JsonElement Root);

/// <summary>A player as the server's <c>modderlords.players</c> reports them.</summary>
public sealed record AdminPlayer(
    string SteamId, string HeroId, string Name, string Clan, int Level, int Gold, bool Online, int PeerId, string Ip, string Busy);

/// <summary>
/// Sends the server's admin commands and matches each <c>@ML@</c> reply to its request by id. The engine's console
/// has no request/reply of its own, so every command carries an id as its first argument and the reply echoes it.
/// <para>Thread-safe: <see cref="Observe"/> is called from the stream-reader thread, the requests from the UI.</para>
/// </summary>
public sealed class AdminClient
{
    public const string Marker = "@ML@";
    public const string CommandGroup = "modderlords";

    private readonly Func<string, Task> _send;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AdminReply>> _pending = new(StringComparer.Ordinal);
    private int _next;

    public AdminClient(Func<string, Task> send) => _send = send;

    /// <summary>Sends <c>modderlords.&lt;command&gt;</c> and waits for its reply. Throws TimeoutException when none comes.</summary>
    public async Task<AdminReply> RequestAsync(string command, IEnumerable<string> args, TimeSpan timeout)
    {
        var id = "r" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<AdminReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            var line = string.Join(" ", new[] { $"{CommandGroup}.{command}", id }.Concat(args.Select(CheckArgument)));
            await _send(line).ConfigureAwait(false);
            var done = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != waiter.Task)
                throw new TimeoutException($"The server did not answer {command} within {timeout.TotalSeconds:0} s. "
                    + "Is the campaign loaded, and is this server running ModderLords' compat module?");
            return await waiter.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Feeds one server stdout line. Returns the reply when it was one (matched to a request or not).</summary>
    public AdminReply? Observe(string line)
    {
        if (Parse(line) is not { } reply) return null;
        if (_pending.TryGetValue(reply.RequestId, out var waiter)) waiter.TrySetResult(reply);
        return reply;
    }

    /// <summary>Fails every request still waiting: the server went away and nothing will answer them.</summary>
    public void Abandon(string why)
    {
        foreach (var kv in _pending) kv.Value.TrySetException(new InvalidOperationException(why));
    }

    /// <summary>The reply on this line, or null for any other line.</summary>
    public static AdminReply? Parse(string line)
    {
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.AsMemory(at + Marker.Length));
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new AdminReply(
                Str(root, "ev"), Str(root, "req"),
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                root);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The players in a <c>players</c> reply.</summary>
    public static IReadOnlyList<AdminPlayer> Players(AdminReply reply)
    {
        if (!reply.Root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new AdminPlayer(
            Str(p, "steamId"), Str(p, "heroId"), Str(p, "name"), Str(p, "clan"), Int(p, "level"), Int(p, "gold"),
            p.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True, Int(p, "peerId", -1), Str(p, "ip"), Str(p, "busy"))).ToList();
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string name, int fallback = 0) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    /// <summary>The console splits arguments on spaces, so one with a space (or nothing) would arrive as something else.</summary>
    private static string CheckArgument(string a) =>
        a.Length == 0 || a.Any(char.IsWhiteSpace) ? throw new ArgumentException($"A server command argument cannot be empty or contain spaces: \"{a}\"") : a;
}
