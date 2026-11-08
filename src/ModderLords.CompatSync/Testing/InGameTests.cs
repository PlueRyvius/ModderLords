using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ModderLords.Testing;

// TaleWorlds- and Coop-free: compiled into ModderLords.Core.Tests too, so the launcher's sequencer is tested against
// this exact agent. How to write a step: docs/INGAME-TEST-HARNESS.md.

/// <summary>Which game a step runs in.</summary>
public enum StepSide { Server, Client, Any }

/// <summary>
/// What a step waits for before it is resumed. Steps yield these; <c>yield return null</c> waits one frame. Build them
/// with <see cref="StepContext"/>'s Until / Settled / Delay.
/// </summary>
public sealed class StepWait
{
    internal Func<bool>? Condition;
    internal Func<object?>? Read;
    internal TimeSpan Quiet;
    internal TimeSpan? Timeout;
    internal DateTime? Until;
    internal string What = "";
    internal StepWait() { }
}

/// <summary>A step: a coroutine on the game's main thread. Returning passes; throwing fails with the message.</summary>
public delegate IEnumerable<StepWait?> StepBody(StepContext ctx);

/// <summary>Everything a running step is given.</summary>
public sealed class StepContext
{
    internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
    internal readonly List<string> Lines = new List<string>();
    internal readonly Action<string, string> DumpSink;

    internal StepContext(string stepId, string side, IReadOnlyDictionary<string, string> args,
        IDictionary<string, object?> state, Action<string, string> dumpSink)
    {
        StepId = stepId;
        Side = side;
        Args = args;
        State = state;
        DumpSink = dumpSink;
    }

    public string StepId { get; }
    /// <summary>"server" or "client:N" in Coop; "game" in a single-player run.</summary>
    public string Side { get; }
    public bool IsServer => Side == "server";
    /// <summary>The run folder, for files the launcher put there for this run (empty outside one).</summary>
    public string RunFolder { get; internal set; } = "";
    /// <summary>The plan's arguments for this step, "$step.key" references already filled in.</summary>
    public IReadOnlyDictionary<string, string> Args { get; }
    /// <summary>Shared by every step of this run on this side (not across sides: use <see cref="Report"/>).</summary>
    public IDictionary<string, object?> State { get; }

    public bool HasArg(string name) => Args.ContainsKey(name);

    public string Arg(string name) =>
        Args.TryGetValue(name, out var value) ? value : throw new StepFailedException($"the plan gives no argument '{name}'");

    public string Arg(string name, string fallback) => Args.TryGetValue(name, out var value) ? value : fallback;

    public int ArgInt(string name) =>
        int.TryParse(Arg(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : throw new StepFailedException($"argument '{name}' is not a whole number: {Arg(name)}");

    public int ArgInt(string name, int fallback) => HasArg(name) ? ArgInt(name) : fallback;

    /// <summary>Fails the step with <paramref name="message"/> unless <paramref name="condition"/> holds.</summary>
    public void Assert(bool condition, string message)
    {
        if (!condition) throw new StepFailedException(message);
    }

    /// <summary>Puts a value in this step's result: later steps get it as "$stepId.key", and compare steps read "value".</summary>
    public void Report(string key, object? value) => Values[key] = Format(value);

    /// <summary>A line in this step's result (and the report).</summary>
    public void Log(string line) => Lines.Add(line);

    /// <summary>Writes evidence next to the report: a state dump, a roster listing.</summary>
    public void Dump(string name, string text) => DumpSink(name, text);

    /// <summary>Resumes when <paramref name="condition"/> holds; fails after <paramref name="timeout"/> (default: the step's own).</summary>
    public StepWait Until(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        new StepWait { Condition = condition, What = what, Timeout = timeout };

    /// <summary>
    /// Resumes once <paramref name="read"/> has returned the same value for <paramref name="quiet"/> (default 2 s): a
    /// sync that has finished, not one still arriving. With <paramref name="expected"/>, the value must also equal it.
    /// </summary>
    public StepWait Settled(Func<object?> read, string what, object? expected = null, TimeSpan? quiet = null, TimeSpan? timeout = null)
    {
        var wait = new StepWait { Read = read, What = what, Quiet = quiet ?? TimeSpan.FromSeconds(2), Timeout = timeout };
        if (expected != null)
        {
            var want = Format(expected);
            wait.Condition = () => Format(read()) == want;
            wait.What = $"{what} to be {want}";
        }
        return wait;
    }

    /// <summary>Resumes after <paramref name="span"/>. Prefer Until or Settled: a fixed wait passes on a fast PC only.</summary>
    public StepWait Delay(TimeSpan span) => new StepWait { Quiet = span, What = "a fixed delay" };

    internal static string Format(object? value) => value switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>Thrown by <see cref="StepContext.Assert"/>: the step fails with the message alone, no stack.</summary>
public sealed class StepFailedException : Exception
{
    public StepFailedException(string message) : base(message) { }
}

/// <summary>
/// The hook API, version <see cref="ApiVersion"/>: register named steps that a test plan runs inside the game. Safe to
/// call at any time, test run or not; registering costs a dictionary entry, and nothing runs unless the launcher started
/// a test run (<see cref="IsActive"/>).
/// </summary>
public static class InGameTests
{
    /// <summary>Bumped only when an existing step or plan would be read differently. New optional members stay v1.</summary>
    public const int ApiVersion = 1;

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Registered> Steps = new Dictionary<string, Registered>(StringComparer.Ordinal);

    internal sealed class Registered
    {
        public Registered(string id, StepSide side, StepBody body) { Id = id; Side = side; Body = body; }
        public string Id { get; }
        public StepSide Side { get; }
        public StepBody Body { get; }
    }

    /// <summary>True in a game the launcher started for a test run.</summary>
    public static bool IsActive { get; internal set; }

    /// <summary>Registers (or replaces) step <paramref name="id"/>; use "module.step", e.g. "stash.putAndDone".</summary>
    public static void Register(string id, StepSide side, StepBody body)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a step needs an id", nameof(id));
        if (body == null) throw new ArgumentNullException(nameof(body));
        lock (Gate) Steps[id] = new Registered(id, side, body);
    }

    internal static Registered? Find(string id)
    {
        lock (Gate) return Steps.TryGetValue(id, out var step) ? step : null;
    }

    internal static List<string> Ids(string side)
    {
        lock (Gate) return Steps.Values.Where(s => Runs(s.Side, side)).Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>A single-player game ("game") is both the world and the player, so every step runs there.</summary>
    internal static bool Runs(StepSide registered, string side) =>
        registered == StepSide.Any || side == "game" || (registered == StepSide.Server) == (side == "server");

    /// <summary>Tests only.</summary>
    internal static void Clear()
    {
        lock (Gate) Steps.Clear();
    }
}
