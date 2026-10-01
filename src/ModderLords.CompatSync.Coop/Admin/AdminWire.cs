using System.Collections.Generic;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>
/// The server-admin console protocol. The launcher sends <c>modderlords.&lt;command&gt; &lt;request id&gt; [args]</c> on the
/// server's stdin; the command answers with one stdout line, <c>@ML@{"ev":...,"req":...,"ok":...}</c>, which the
/// launcher matches to its request by id. The engine's console has no request/reply of its own, so the id is ours.
/// <para>No game types here: the launcher's tests compile this file to check both ends agree.</para>
/// </summary>
public static class AdminWire
{
    public const string Marker = "@ML@";
    public const string CommandGroup = "modderlords";

    /// <summary>The ban list, relative to the server's user folder (BANNERLORD_USER_DIR). The launcher writes it; the server reads it.</summary>
    public const string BansFile = "ModderLords\\bans.json";

    /// <summary>One reply line. <paramref name="error"/> null means success; <paramref name="data"/>'s keys go next to ev/req/ok.</summary>
    public static string Reply(string ev, string req, string? error, IDictionary<string, object?>? data = null)
    {
        var o = new Dictionary<string, object?> { ["ev"] = ev, ["req"] = req, ["ok"] = error is null };
        if (error is not null) o["error"] = error;
        if (data is not null)
            foreach (var kv in data) o[kv.Key] = kv.Value;
        return Marker + MiniJson.SerializeCompact(o);
    }

    /// <summary>The request id a command was given (its first argument), or "-" when typed by hand without one.</summary>
    public static string RequestId(IList<string> args) => args.Count > 0 && args[0].Length > 0 ? args[0] : "-";
}
