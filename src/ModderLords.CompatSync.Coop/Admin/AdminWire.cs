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

/// <summary>
/// Text for <c>modderlords.announce &lt;req&gt; &lt;hex&gt;</c>, shown on every player's screen. The console splits its
/// arguments on spaces, so the text travels as the hex of its UTF-8 bytes.
/// </summary>
public static class AnnounceWire
{
    public const int MaxLength = 200;

    public static string Encode(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Trim());
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>False for anything that is not hex of valid UTF-8, or is empty or too long.</summary>
    public static bool TryDecode(string hex, out string text)
    {
        text = "";
        if (hex.Length == 0 || hex.Length % 2 != 0) return false;
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out bytes[i])) return false;
        try { text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).Trim(); }
        catch (System.ArgumentException) { return false; }
        return text.Length > 0 && text.Length <= MaxLength;
    }
}
