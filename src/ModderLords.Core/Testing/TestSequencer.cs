using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModderLords.Core.Smoke;

namespace ModderLords.Core.Testing;

/// <summary>How the launcher talks to the games of a run: lines out to every game, lines back from each side.</summary>
public interface ITestRunChannel
{
    /// <summary>Appends one control line (every game reads all of them and acts on those addressed to it).</summary>
    void Send(string line);
    /// <summary>The lines this side has written since the last call.</summary>
    IReadOnlyList<string> Receive(string side);
}

/// <summary>
/// The run folder (docs/INGAME-TEST-HARNESS.md): <c>run.json</c> and <c>control.jsonl</c> written here, one
/// <c>results-&lt;side&gt;.jsonl</c> written by each game. One writer per file, append-only, so nothing needs a lock.
/// </summary>
public sealed class FileTestRunChannel : ITestRunChannel
{
    private readonly Dictionary<string, LogTail> _tails = new(StringComparer.Ordinal);

    private FileTestRunChannel(string folder, string runId, string token) { Folder = folder; RunId = runId; Token = token; }

    public string Folder { get; }
    public string RunId { get; }
    /// <summary>Must match the one each game was started with, so a game only joins the run it was started for.</summary>
    public string Token { get; }

    /// <summary>Creates a new run folder under <paramref name="root"/> with its run.json.</summary>
    public static FileTestRunChannel Create(string root, TestPlan plan, DateTimeOffset now)
    {
        var runId = $"{now:yyyyMMdd-HHmmss}-{plan.Name}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
        var folder = Path.Combine(root, runId);
        Directory.CreateDirectory(folder);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // The in-game step API, not the suite format: v2 suites changed what the launcher reads, not what a game runs.
        var run = new JsonObject { ["api"] = TestPlan.InGameApi, ["runId"] = runId, ["suite"] = plan.Name, ["token"] = token };
        if (plan.Loads.Count > 0)
            run["load"] = new JsonArray(plan.Loads.Select(l => (JsonNode)new JsonObject
            {
                ["module"] = l.Module, ["dll"] = l.Dll, ["type"] = l.Type,
                ["requires"] = new JsonArray(l.Requires.Select(r => (JsonNode?)r).ToArray()),
            }).ToArray());
        File.WriteAllText(Path.Combine(folder, "run.json"), run.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "control.jsonl"), "");
        return new FileTestRunChannel(folder, runId, token);
    }

    /// <summary>The value of the switch a game is started with: the player's game as <c>/modderlords-test VALUE</c>, the server as MODDERLORDS_TEST_RUN.</summary>
    public string SwitchValue(string side) => $"{Folder}|{side}|{Token}";

    public const string ClientArgument = "/modderlords-test";
    public const string ServerVariable = "MODDERLORDS_TEST_RUN";

    public static string ResultsFile(string side) => "results-" + side.Replace(':', '-') + ".jsonl";

    public void Send(string line) => File.AppendAllText(Path.Combine(Folder, "control.jsonl"), line + "\n", new UTF8Encoding(false));

    public IReadOnlyList<string> Receive(string side)
    {
        if (!_tails.TryGetValue(side, out var tail))
            _tails[side] = tail = new LogTail(Path.Combine(Folder, ResultsFile(side)), fromStart: true);
        return tail.ReadNew();
    }
}

public sealed record TestSequencerOptions
{
    /// <summary>How long a side may take to announce itself once the run starts.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Added to a step's own timeout (which the game enforces) before the launcher gives up on a result.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Runs a plan's steps in order against the games of a run, one at a time, and builds the report. Pure apart from the
/// channel, so it is tested against the real in-game agent with no game.
/// </summary>
public sealed class TestSequencer
{
    private readonly ITestRunChannel _channel;
    private readonly TestSequencerOptions _options;
    private readonly Dictionary<string, Queue<JsonObject>> _inbox = new(StringComparer.Ordinal);

    public TestSequencer(ITestRunChannel channel, TestSequencerOptions? options = null)
    {
        _channel = channel;
        _options = options ?? new TestSequencerOptions();
    }

    public event Action<string>? Progress;

    /// <summary>Called while waiting; returns a reason to stop (a game exited) or null to keep waiting.</summary>
    public Func<string?>? Abort { get; init; }

    public async Task<TestReport> RunAsync(TestPlan plan, string runId, CancellationToken cancel)
    {
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var steps = new List<StepReport>();
        var notes = new List<string>();
        var versions = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        long seq = 0;

        // 1. Every side announces itself, with the steps it has.
        var registered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? stopReason = null;
        foreach (var side in plan.Sides)
        {
            Say($"Waiting for {side} to join the run…");
            var hello = await Next(side, "hello", null, _options.HelloTimeout, cancel);
            if (hello is null) { stopReason = $"{side} never joined the run (no hello within {_options.HelloTimeout.TotalSeconds:F0} s): is ModderLords.CompatSync loaded there, and was it started with the test switch?"; break; }
            if (hello["versions"] is JsonObject v) versions[side] = v.ToDictionary(kv => kv.Key, kv => Str(kv.Value));
            if (Str(hello["refused"]) is { Length: > 0 } refused) { stopReason = $"{side} refused the run: {refused}"; break; }
            foreach (var skipped in hello["skipped"] as JsonArray ?? []) notes.Add($"{side}: {Str(skipped)}");
            if (hello["loadErrors"] is JsonArray { Count: > 0 } loadErrors) { stopReason = $"{side} could not load a test module: {string.Join("; ", loadErrors.Select(Str))}"; break; }
            registered[side] = (hello["steps"] as JsonArray ?? []).Select(Str).ToHashSet(StringComparer.Ordinal);
        }
        if (stopReason is null)
        {
            // Every step the plan names must exist where it runs: one missing step is better found now than mid-run.
            var missing = plan.Steps.SelectMany(s => (s.IsCompare ? s.CompareSides : [s.Side]).Select(side => (s.Id, side)))
                .Where(x => !registered[x.side].Contains(x.Id)).Distinct().Select(x => $"{x.Id} on {x.side}").ToList();
            if (missing.Count > 0) stopReason = "not registered in the game: " + string.Join(", ", missing);
        }
        if (stopReason is not null) notes.Add(stopReason);

        // 2. The steps, in order. In a run of several suites a failure skips the rest of its own suite only; the next
        // suite starts with its own world setup. A game that stopped answering ends the whole run.
        string? failedSuite = null;
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var sideText = step.IsCompare ? string.Join(" / ", step.CompareSides) : step.Side;
            if (stopReason is not null || cancel.IsCancellationRequested || (failedSuite is not null && step.Suite == failedSuite))
            {
                steps.Add(new StepReport { Index = i + 1, Step = step.Id, Side = sideText, Outcome = StepOutcome.Skipped, KnownFailing = step.KnownFailing, Suite = step.Suite });
                continue;
            }
            Say($"{i + 1}/{plan.Steps.Count}: {step.Describe()}");
            StepReport report;
            if (!TrySubstitute(step.Args, values, out var args, out var unresolved))
                report = new StepReport { Index = i + 1, Step = step.Id, Side = sideText, Outcome = StepOutcome.Fail, Args = step.Args, Message = unresolved! };
            else if (!step.IsCompare)
                report = await RunOne(i + 1, step, step.Side, args, ++seq, cancel);
            else
            {
                var each = new List<StepReport>();
                foreach (var side in step.CompareSides)
                {
                    var one = await RunOne(i + 1, step, side, args, ++seq, cancel);
                    each.Add(one);
                    if (one.Outcome != StepOutcome.Pass) break;
                }
                report = Compare(i + 1, step, each, args);
            }
            report = report with { KnownFailing = step.KnownFailing, Outcome = Known(report.Outcome, step.KnownFailing), Suite = step.Suite };
            if (report.Outcome == StepOutcome.UnexpectedPass) report = report with { Message = "marked known-failing but passed: the fix may have landed; remove the mark" };
            foreach (var (key, value) in report.Values) values[$"{step.Name}.{key}"] = value;
            steps.Add(report);
            Say($"   {report.Outcome}" + (report.Message.Length > 0 ? ": " + report.Message : ""));

            if (report.Outcome == StepOutcome.NoResult) stopReason = $"no result from {sideText}";
            else if (report.Outcome is StepOutcome.Fail or StepOutcome.UnexpectedPass && !step.ContinueOnFail)
            {
                if (step.Suite.Length > 0) failedSuite = step.Suite;
                else stopReason = $"step {i + 1} failed";
            }
        }
        _channel.Send(new JsonObject { ["op"] = "end" }.ToJsonString());
        if (cancel.IsCancellationRequested) notes.Add("The run was cancelled.");
        return new TestReport
        {
            Suite = plan.Name,
            RunId = runId,
            Started = started,
            Duration = clock.Elapsed,
            Steps = steps,
            Versions = versions,
            Notes = notes,
            Worlds = plan.Worlds,
        };
    }

    private async Task<StepReport> RunOne(int index, PlanStep step, string side, IReadOnlyDictionary<string, string> args, long seq, CancellationToken cancel)
    {
        var argsNode = new JsonObject();
        foreach (var (key, value) in args) argsNode[key] = value;
        _channel.Send(new JsonObject
        {
            ["op"] = "step",
            ["seq"] = seq,
            ["side"] = side,
            ["step"] = step.Id,
            ["args"] = argsNode,
            ["timeoutMs"] = (long)step.Timeout.TotalMilliseconds,
        }.ToJsonString());
        var result = await Next(side, "result", seq, step.Timeout + _options.Grace, cancel);
        if (result is null)
            return new StepReport
            {
                Index = index, Step = step.Id, Side = side, Args = args, Outcome = cancel.IsCancellationRequested ? StepOutcome.Skipped : StepOutcome.NoResult,
                Message = _abortReason ?? $"no result within {(step.Timeout + _options.Grace).TotalSeconds:F0} s: the game may have crashed or hung",
            };
        return new StepReport
        {
            Index = index,
            Step = step.Id,
            Side = side,
            Args = args,
            Outcome = Str(result["status"]) == "pass" ? StepOutcome.Pass : StepOutcome.Fail,
            Message = Str(result["message"]),
            Values = (result["values"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => Str(kv.Value)),
            Log = (result["log"] as JsonArray ?? []).Select(Str).ToList(),
            Milliseconds = long.TryParse(Str(result["ms"]), out var ms) ? ms : 0,
        };
    }

    /// <summary>A compare passes when every side ran the step and reported the same <c>value</c>.</summary>
    internal static StepReport Compare(int index, PlanStep step, IReadOnlyList<StepReport> each, IReadOnlyDictionary<string, string> args)
    {
        var sideText = string.Join(" / ", step.CompareSides);
        var log = each.SelectMany(r => r.Log.Select(l => $"{r.Side}: {l}")).ToList();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in each)
            foreach (var (key, value) in r.Values) values[$"{key}@{r.Side}"] = value;
        if (each.FirstOrDefault(r => r.Outcome != StepOutcome.Pass) is { } bad)
            return new StepReport { Index = index, Step = step.Id, Side = sideText, Args = args, Outcome = bad.Outcome, Values = values, Log = log, Message = $"{bad.Side}: {bad.Message}" };
        var read = each.Select(r => (r.Side, Value: r.Values.TryGetValue("value", out var v) ? v : null)).ToList();
        if (read.Where(r => r.Value is null).Select(r => r.Side).FirstOrDefault() is { } silent)
            return new StepReport { Index = index, Step = step.Id, Side = sideText, Args = args, Outcome = StepOutcome.Fail, Values = values, Log = log, Message = $"{silent} reported no \"value\" to compare" };
        var agree = read.Select(r => r.Value).Distinct().Count() == 1;
        values["value"] = agree ? read[0].Value! : "";
        return new StepReport
        {
            Index = index, Step = step.Id, Side = sideText, Args = args, Values = values, Log = log,
            Milliseconds = each.Sum(r => r.Milliseconds),
            Outcome = agree ? StepOutcome.Pass : StepOutcome.Fail,
            Message = agree ? $"all read {read[0].Value}" : "the sides disagree: " + string.Join(", ", read.Select(r => $"{r.Side} {r.Value}")),
        };
    }

    internal static StepOutcome Known(StepOutcome outcome, string? knownFailing) => (outcome, knownFailing) switch
    {
        (_, null) => outcome,
        (StepOutcome.Fail, _) => StepOutcome.KnownFail,
        (StepOutcome.Pass, _) => StepOutcome.UnexpectedPass,
        _ => outcome,
    };

    /// <summary>Fills "$step.key" arguments from earlier steps' values; false with the reason when one has no value.</summary>
    internal static bool TrySubstitute(IReadOnlyDictionary<string, string> args, IReadOnlyDictionary<string, string> values,
        out IReadOnlyDictionary<string, string> filled, out string? unresolved)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        unresolved = null;
        foreach (var (key, value) in args)
        {
            if (TestPlan.Reference(value) is { } r)
            {
                if (!values.TryGetValue($"{r.Step}.{r.Key}", out var v)) { unresolved ??= $"argument {key}: {value} has no value (step {r.Step} reported no \"{r.Key}\")"; continue; }
                result[key] = v;
            }
            else result[key] = value;
        }
        filled = result;
        return unresolved is null;
    }

    private string? _abortReason;

    /// <summary>The next line of <paramref name="op"/> from <paramref name="side"/> (for a result, the one for <paramref name="seq"/>).</summary>
    private async Task<JsonObject?> Next(string side, string op, long? seq, TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (!_inbox.TryGetValue(side, out var queue)) _inbox[side] = queue = new Queue<JsonObject>();
            foreach (var line in _channel.Receive(side))
            {
                try { if (JsonNode.Parse(line) is JsonObject o) queue.Enqueue(o); }
                catch (JsonException) { Say($"unreadable line from {side}: {line}"); }
            }
            while (queue.Count > 0)
            {
                var o = queue.Dequeue();
                if (Str(o["op"]) != op) continue;
                if (seq is { } want && Str(o["seq"]) != want.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
                return o;
            }
            if (deadline.Elapsed > timeout || cancel.IsCancellationRequested) return null;
            if (Abort?.Invoke() is { } reason) { _abortReason = reason; return null; }
            try { await Task.Delay(_options.Poll, cancel); } catch (OperationCanceledException) { }
        }
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private void Say(string line) => Progress?.Invoke(line);
}
