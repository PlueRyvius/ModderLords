using System.Globalization;

namespace ModularCoop.Core.Logs;

public enum PerfKind
{
    /// <summary>Frame/tick timing from the guards module.</summary>
    Tick,
    /// <summary>Connected players, from the Coop adapter when Settings sync is on.</summary>
    Players,
    /// <summary>Unrecognised kind; kept rather than dropped so a newer module can talk to an older launcher.</summary>
    Unknown,
}

/// <summary>
/// One performance line from a module, parsed. Fields stay as strings and are read through the typed accessors, so a
/// truncated or malformed line degrades to a missing value instead of throwing on the process read thread.
/// </summary>
public sealed record PerfSample(DateTimeOffset At, PerfKind Kind, IReadOnlyDictionary<string, string> Fields)
{
    public double? Number(string key) =>
        Fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
            ? d : null;

    public int? Count(string key) =>
        Fields.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public string? Text(string key) => Fields.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Reads the one-line-per-window format the modules print, e.g.
/// <c>[ModularCoop] perf tick avgFps=142.7 minFps=38.2 frames=1427 windowSec=10.0 campaignMode=Real</c>
/// Deliberately forgiving: anything it cannot make sense of returns null rather than throwing.
/// </summary>
public static class PerfLineParser
{
    public const string Prefix = "[ModularCoop] perf ";

    public static PerfSample? TryParse(string line, DateTimeOffset at)
    {
        if (line is null) return null;
        var t = line.Trim();
        if (!t.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var parts = t[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var kind = parts[0] switch
        {
            "tick" => PerfKind.Tick,
            "players" => PerfKind.Players,
            _ => PerfKind.Unknown,
        };

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;   // "junk", "=x", "x=" are all skipped, not fatal
            fields[part[..eq]] = part[(eq + 1)..];
        }
        return fields.Count == 0 && kind == PerfKind.Unknown ? null : new PerfSample(at, kind, fields);
    }
}
