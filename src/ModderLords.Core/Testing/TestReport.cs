using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Testing;

public enum StepOutcome
{
    Pass,
    Fail,
    /// <summary>Failed, and the plan marks it as expected to until an upstream fix.</summary>
    KnownFail,
    /// <summary>Marked known-failing, but passed: the upstream fix may have landed. Counts as a failure so it is noticed.</summary>
    UnexpectedPass,
    /// <summary>No result arrived in time: the game crashed, hung or never started the step.</summary>
    NoResult,
    /// <summary>Not run, because an earlier step failed.</summary>
    Skipped,
}

public sealed record StepReport
{
    public required int Index { get; init; }
    public required string Step { get; init; }
    public required string Side { get; init; }
    public required StepOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Log { get; init; } = [];
    public long Milliseconds { get; init; }
    public string? KnownFailing { get; init; }
}

public sealed record TestReport
{
    public required string Suite { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset Started { get; init; }
    public TimeSpan Duration { get; init; }
    public required IReadOnlyList<StepReport> Steps { get; init; }
    /// <summary>Per side: game, Coop and ModderLords versions as that game reported them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Versions { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    public IReadOnlyList<string> Notes { get; init; } = [];

    [JsonIgnore]
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Outcome is StepOutcome.Pass or StepOutcome.KnownFail);

    public string Verdict => Passed ? "PASS" : "FAIL";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test suite {Suite}: {Verdict}   (run {RunId}, {Duration.TotalSeconds:F0} s)");
        foreach (var (side, versions) in Versions)
            sb.AppendLine($"  {side,-9} " + string.Join(", ", versions.Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var step in Steps)
        {
            var mark = step.Outcome switch
            {
                StepOutcome.Pass => "pass",
                StepOutcome.KnownFail => "known",
                StepOutcome.Skipped => "skip",
                StepOutcome.UnexpectedPass => "PASS?",
                StepOutcome.NoResult => "NONE",
                _ => "FAIL",
            };
            sb.AppendLine($"  [{mark,-5}] {step.Index,2}. {step.Step} on {step.Side}" + (step.Message.Length > 0 ? " - " + step.Message : ""));
            if (step.KnownFailing is { } issue && step.Outcome is StepOutcome.KnownFail or StepOutcome.UnexpectedPass) sb.AppendLine("           known failing: " + issue);
            if (step.Outcome is not StepOutcome.Pass and not StepOutcome.Skipped)
                foreach (var line in step.Log) sb.AppendLine("           " + line);
        }
        foreach (var note in Notes) sb.AppendLine("  note: " + note);
        return sb.ToString();
    }
}
