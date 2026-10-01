using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModderLords.Core.Smoke;

/// <summary>One line of Coop's own log (Coop_client.log): <c>[(53960) 01:52:13 INF Source] message</c>.</summary>
public sealed record CoopLogLine(int Pid, string Time, string Level, string Source, string Message);

/// <summary>One player in the dedicated server's <c>@DS@{"ev":"players",...}</c> event.</summary>
public sealed record ServerPlayer(int Id, string Name, string State, string Address);

/// <summary>How far a joining game has got, from Coop's client log. Later stages compare greater.</summary>
public enum ClientStage
{
    NotStarted,
    Started,
    Connecting,
    ReceivingWorld,
    WorldReceived,
    Loading,
    CharacterCreation,
    OnMap,
}

/// <summary>
/// Reads the lines a smoke test decides from. Every pattern here was taken from a real session's logs (Coop 0.1.5,
/// Bannerlord 1.4.8, the DedicatedServer package of 2026-09); a change upstream shows up as a stage the test never
/// reaches, with the log slice saved next to the report, rather than as a wrong pass.
/// </summary>
public static partial class SmokeSignals
{
    // ---- Coop's client log ------------------------------------------------------------------------------------

    [GeneratedRegex(@"^\[\((\d+)\) (\d\d:\d\d:\d\d) ([A-Z]{3}) ([^\]]*)\] ?(.*)$")]
    private static partial Regex CoopLineRx();

    public static CoopLogLine? ParseCoopLine(string line)
    {
        var m = CoopLineRx().Match(line.TrimEnd('\r'));
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        return new CoopLogLine(pid, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value);
    }

    /// <summary>The stage a Coop client line marks, or null for the thousands that mark nothing.</summary>
    public static ClientStage? StageOf(CoopLogLine line)
    {
        var m = line.Message;
        if (m.StartsWith("Attempting connection to", StringComparison.Ordinal)) return ClientStage.Connecting;
        if (m.StartsWith("Receiving host save transfer", StringComparison.Ordinal)) return ClientStage.ReceivingWorld;
        if (m.StartsWith("Received host save transfer", StringComparison.Ordinal)) return ClientStage.WorldReceived;
        if (m.StartsWith("Game State is changing to ", StringComparison.Ordinal))
        {
            var state = m["Game State is changing to ".Length..].Trim();
            if (state == "MapState") return ClientStage.OnMap;
            if (state.Contains("CharacterCreation", StringComparison.Ordinal)) return ClientStage.CharacterCreation;
            if (state == "GameLoadingState") return ClientStage.Loading;
        }
        return null;
    }

    [GeneratedRegex(@"^\[Fps\] .*\bavg=([0-9.]+)")]
    private static partial Regex FpsRx();

    /// <summary>Coop's own 30-second frame-rate line: <c>[Fps] frames=4357 seconds=30.01 avg=145.2 ...</c>.</summary>
    public static double? FpsAverage(CoopLogLine line)
    {
        var m = FpsRx().Match(line.Message);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Coop logs thousands of ERR lines in a healthy session ("Client updated managed _isDisorganized"), so its error
    /// level decides nothing on its own. Fatal lines and unhandled exceptions do.
    /// </summary>
    public static bool IsCoopFatal(CoopLogLine line) =>
        line.Level == "FTL" || line.Message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase);

    // ---- the dedicated server's structured events ---------------------------------------------------------------

    public const string ServerEventMarker = "@DS@";

    /// <summary>The player list from a <c>@DS@{"ev":"players","list":[...]}</c> line; null for any other line.</summary>
    public static IReadOnlyList<ServerPlayer>? ParsePlayers(string line)
    {
        var at = line.IndexOf(ServerEventMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line[(at + ServerEventMarker.Length)..]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ev", out var ev) || ev.GetString() != "players") return null;
            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            var players = new List<ServerPlayer>();
            foreach (var p in list.EnumerateArray())
                players.Add(new ServerPlayer(
                    p.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) ? i : -1,
                    p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    p.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "",
                    p.TryGetProperty("addr", out var a) ? a.GetString() ?? "" : ""));
            return players;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The server's player state that means "playing on the campaign map".</summary>
    public const string OnMapState = "on map";

    public static bool IsServing(string line) => line.Contains("SERVING", StringComparison.Ordinal);

    [GeneratedRegex(@"pulse: time=(Spring|Summer|Autumn|Winter) (\d+), (\d+) timeMode=(\S+)")]
    private static partial Regex PulseRx();

    /// <summary>
    /// The server's periodic status line, <c>[DedicatedServer] pulse: time=Autumn 1, 1084 timeMode=Stop players=1 ...</c>
    /// (about every 14 s): the campaign date as a day number (84-day years, 21-day seasons) and the time mode.
    /// </summary>
    public static (string Date, int Day, string TimeMode)? ParsePulse(string line)
    {
        var m = PulseRx().Match(line);
        if (!m.Success) return null;
        var season = Array.IndexOf(Seasons, m.Groups[1].Value);
        var day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return ($"{m.Groups[1].Value} {day}, {year}", year * 84 + season * 21 + day - 1, m.Groups[4].Value);
    }

    private static readonly string[] Seasons = ["Spring", "Summer", "Autumn", "Winter"];

    /// <summary>Coop's server command that runs campaign time; the smoke test sends it once the game is on the map.</summary>
    public const string RunTimeCommand = "coop.debug.set_time_mode";

    // ---- ModderLords' own lines (compat module, both sides) -----------------------------------------------------

    public const string CompatTag = "[ModderLords.Compat] ";

    // Mirrors ModderLords.CompatSync.Coop.SessionCheck; the module cannot be referenced from here.
    public const string ReadyLine = "session check: campaign ready on this player's game";
    public const string AnsweredLine = "session check: server answered the ping in ";
    public const string ServerAnsweredLine = "session check: answered the ping of ";

    /// <summary>The message part of a compat log line (<c>01:51:30.120 [ModderLords.Compat] ...</c>), or null.</summary>
    public static string? CompatMessage(string line)
    {
        var at = line.IndexOf(CompatTag, StringComparison.Ordinal);
        return at < 0 ? null : line[(at + CompatTag.Length)..].TrimEnd('\r');
    }

    public static bool IsWarning(string compatMessage) => compatMessage.StartsWith("WARNING", StringComparison.Ordinal);

    /// <summary>
    /// Warnings that are true by design in the run that prints them: the Bellum validation switch announces itself on
    /// every launch it is used in. Listed in the report, not counted against it.
    /// </summary>
    public static bool IsExpectedWarning(string compatMessage) =>
        compatMessage.Contains("isolated validation run", StringComparison.Ordinal)
        || compatMessage.Contains("isolated validation process", StringComparison.Ordinal);

    [GeneratedRegex(@"server answered the ping in (\d+) ms")]
    private static partial Regex PingRx();

    public static int? PingMs(string compatMessage)
    {
        var m = PingRx().Match(compatMessage);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null;
    }
}
