using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Smoke;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmokeVerdict
{
    Pass,
    /// <summary>Not a failure of the build, but the test could not prove what it was there for (e.g. no character yet).</summary>
    Warn,
    Fail,
    /// <summary>Could not be checked because an earlier step did not get there.</summary>
    Skipped,
    /// <summary>A measurement, not a judgement.</summary>
    Info,
}

/// <summary>One line of the checklist: what was checked, the verdict, one sentence, and the lines that decided it.</summary>
public sealed record SmokeCheck(string Name, SmokeVerdict Verdict, string Detail, IReadOnlyList<string> Evidence)
{
    public SmokeCheck(string name, SmokeVerdict verdict, string detail) : this(name, verdict, detail, []) { }
}

public sealed record SmokeReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    SmokeVerdict Overall,
    IReadOnlyList<SmokeCheck> Checks,
    IReadOnlyList<string> Notes)
{
    /// <summary>Where the report and the log slices were written; null until saved.</summary>
    public string? Folder { get; init; }

    public static SmokeVerdict OverallOf(IEnumerable<SmokeCheck> checks)
    {
        var list = checks.ToList();
        if (list.Any(c => c.Verdict == SmokeVerdict.Fail)) return SmokeVerdict.Fail;
        if (list.Any(c => c.Verdict == SmokeVerdict.Warn)) return SmokeVerdict.Warn;
        return SmokeVerdict.Pass;
    }

    public string Headline => Overall switch
    {
        SmokeVerdict.Pass => "PASSED",
        SmokeVerdict.Warn => "PASSED WITH WARNINGS",
        _ => "FAILED",
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ModderLords smoke test — {Headline}");
        sb.AppendLine($"started {StartedAt:yyyy-MM-dd HH:mm:ss}, took {Duration:mm\\:ss}");
        sb.AppendLine();
        foreach (var c in Checks)
        {
            sb.AppendLine($"[{Label(c.Verdict),-4}] {c.Name}: {c.Detail}");
            foreach (var e in c.Evidence) sb.AppendLine("         " + e);
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in Notes) sb.AppendLine("note: " + n);
        }
        if (Folder is not null) sb.AppendLine().AppendLine("logs: " + Folder);
        return sb.ToString();
    }

    public static string Label(SmokeVerdict v) => v switch
    {
        SmokeVerdict.Pass => "ok",
        SmokeVerdict.Warn => "warn",
        SmokeVerdict.Fail => "FAIL",
        SmokeVerdict.Skipped => "skip",
        _ => "info",
    };

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
