using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ModderLords.CompatSync;

namespace ModderLords.Testing;

/// <summary>
/// The test-run switch, which only the launcher's <c>test</c> command sets: the player's game gets the launch argument
/// <c>/modderlords-test "folder|client:1|token"</c> (<c>game</c> in a single-player run, where no server is started),
/// the server the environment variable <c>MODDERLORDS_TEST_RUN</c>
/// with the same value (through its launch plan only, so a normal Host launch can never carry it).
/// </summary>
internal sealed class TestRunSwitch
{
    public const string Argument = "/modderlords-test";
    public const string Variable = "MODDERLORDS_TEST_RUN";

    private TestRunSwitch(string folder, string side, string token) { Folder = folder; Side = side; Token = token; }
    public string Folder { get; }
    public string Side { get; }
    public string Token { get; }

    /// <summary>The switch from this process's arguments or (server only) environment, or null when this is not a test run.</summary>
    public static TestRunSwitch? From(string[] args, string? environmentValue, bool isServer)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(Argument, StringComparison.OrdinalIgnoreCase)) return Parse(args[i + 1]);
        return isServer && !string.IsNullOrEmpty(environmentValue) ? Parse(environmentValue!) : null;
    }

    public static TestRunSwitch? Parse(string value)
    {
        var parts = value.Trim().Trim('"').Split('|');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)) return null;
        var side = parts[1];
        if (side != "server" && side != "game" && !(side.StartsWith("client:", StringComparison.Ordinal) && int.TryParse(side.Substring(7), out var n) && n > 0)) return null;
        return new TestRunSwitch(parts[0], side, parts[2]);
    }
}

/// <summary>
/// One game's half of a test run. Reads the launcher's <c>control.jsonl</c> in the run folder, runs the steps addressed
/// to its side one at a time, and appends one line per step to <c>results-&lt;side&gt;.jsonl</c>. File formats:
/// docs/INGAME-TEST-HARNESS.md. TaleWorlds-free; the game's part (clock, screenshot, versions) is passed in.
/// </summary>
internal sealed class TestAgent
{
    private readonly TestRunSwitch _switch;
    private readonly Action<string> _log;
    private readonly Func<string, bool>? _screenshot;
    private readonly Func<IDictionary<string, string>> _versions;
    private readonly Func<string, bool> _moduleLoaded;
    private readonly string _resultsPath;
    private readonly Dictionary<string, object?> _state = new Dictionary<string, object?>(StringComparer.Ordinal);
    private long _controlOffset;
    private readonly MemoryStream _partial = new MemoryStream();
    private readonly Queue<string> _pending = new Queue<string>();
    private bool _helloSent, _ended, _refused;
    private StepRunner? _running;
    private long _runningSeq;
    private DateTime _runningStarted;

    /// <param name="moduleLoaded">Whether this game loaded a module by id, for module step DLLs that need a mod.</param>
    public TestAgent(TestRunSwitch runSwitch, Action<string> log, Func<string, bool>? screenshot = null, Func<IDictionary<string, string>>? versions = null,
        Func<string, bool>? moduleLoaded = null)
    {
        _moduleLoaded = moduleLoaded ?? (_ => false);
        _switch = runSwitch;
        _log = log;
        _screenshot = screenshot;
        _versions = versions ?? (() => new Dictionary<string, string>());
        _resultsPath = Path.Combine(runSwitch.Folder, ResultsFile(runSwitch.Side));
    }

    public static string ResultsFile(string side) => "results-" + side.Replace(':', '-') + ".jsonl";
    public bool Ended => _ended || _refused;

    /// <summary>Every frame on the game's main thread.</summary>
    public void Tick(DateTime now)
    {
        if (Ended) return;
        try
        {
            if (!_helloSent && !Hello()) return;
            if (_running != null)
            {
                var outcome = _running.Tick(now);
                if (outcome != null) Finish(outcome, now);
                return;
            }
            ReadControl();
            while (_pending.Count > 0)
            {
                var line = _pending.Dequeue();
                Dictionary<string, object?> command;
                try { command = MiniJson.ParseObject(line); }
                catch (FormatException) { _log("test run: unreadable control line: " + line); continue; }
                var op = MiniJson.GetString(command, "op");
                if (op == "end") { _ended = true; _log("test run: ended by the launcher"); return; }
                if (op != "step" || MiniJson.GetString(command, "side") != _switch.Side) continue;
                Start(command, now);
                if (_running != null) return;   // the rest waits until this step is done
            }
        }
        catch (Exception ex)
        {
            // An agent that throws every frame would flood the log; one failure ends this side's part of the run.
            _log("test run: agent failed, this side stops: " + ex);
            _ended = true;
        }
    }

    private bool Hello()
    {
        var runPath = Path.Combine(_switch.Folder, "run.json");
        if (!File.Exists(runPath)) return false;
        var run = MiniJson.ParseObject(File.ReadAllText(runPath));
        var (skipped, loadErrors) = MiniJson.GetString(run, "token") == _switch.Token ? LoadModules(run) : (new List<object?>(), new List<object?>());
        var hello = new Dictionary<string, object?>
        {
            ["op"] = "hello",
            ["side"] = _switch.Side,
            ["api"] = (long)InGameTests.ApiVersion,
            ["steps"] = InGameTests.Ids(_switch.Side).Cast<object?>().ToList(),
            ["versions"] = _versions().ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };
        if (skipped.Count > 0) hello["skipped"] = skipped;
        if (loadErrors.Count > 0) hello["loadErrors"] = loadErrors;
        if (MiniJson.GetString(run, "token") != _switch.Token)
        {
            hello["refused"] = "the run folder's token does not match this game's switch";
            _refused = true;
        }
        else if (long.TryParse(MiniJson.GetString(run, "api"), out var api) && api > InGameTests.ApiVersion)
        {
            hello["refused"] = $"the plan needs test API v{api}; this game has v{InGameTests.ApiVersion}";
            _refused = true;
        }
        Append(hello);
        _helloSent = true;
        _log(_refused ? "test run: refused: " + hello["refused"] : $"TEST MODE: this game is running a ModderLords test run as {_switch.Side} ({_switch.Folder})");
        if (!_refused) InGameTests.IsActive = true;
        return !_refused;
    }

    /// <summary>
    /// Loads the run's module step DLLs (run.json "load", decision 30) whose mods this game has, and registers their
    /// steps. A side without a module's mods skips it; a DLL that will not load, or registers nothing, is an error the
    /// launcher stops the run on.
    /// </summary>
    private (List<object?> Skipped, List<object?> Errors) LoadModules(Dictionary<string, object?> run)
    {
        var skipped = new List<object?>();
        var errors = new List<object?>();
        foreach (var entry in MiniJson.GetArray(run, "load") ?? new List<object?>())
        {
            if (!(entry is Dictionary<string, object?> load)) continue;
            var module = MiniJson.GetString(load, "module") ?? "?";
            var missing = (MiniJson.GetArray(load, "requires") ?? new List<object?>()).Select(r => r as string ?? "").Where(r => r.Length > 0 && !_moduleLoaded(r)).ToList();
            if (missing.Count > 0) { skipped.Add($"test module {module} not loaded here: this game has no {string.Join(", ", missing)}"); continue; }
            var dll = MiniJson.GetString(load, "dll") ?? "";
            var typeName = MiniJson.GetString(load, "type") ?? "";
            try
            {
                var before = InGameTests.Ids(_switch.Side).Count;
                var type = System.Reflection.Assembly.LoadFrom(dll).GetType(typeName, throwOnError: true)!;
                var register = type.GetMethod("Register", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException(typeName + " has no public static Register()");
                register.Invoke(null, null);
                var added = InGameTests.Ids(_switch.Side).Count - before;
                if (added <= 0) errors.Add($"{module}: {typeName}.Register() registered no steps for {_switch.Side}");
                else _log($"test run: test module {module} loaded ({added} step(s)) from {dll}");
            }
            catch (Exception ex) { errors.Add($"{module}: {Path.GetFileName(dll)}: {ex.GetBaseException().Message}"); }
        }
        return (skipped, errors);
    }

    private void Start(Dictionary<string, object?> command, DateTime now)
    {
        var id = MiniJson.GetString(command, "step") ?? "";
        _runningSeq = long.TryParse(MiniJson.GetString(command, "seq"), out var seq) ? seq : 0;
        var args = (MiniJson.GetObject(command, "args") ?? new Dictionary<string, object?>())
            .ToDictionary(kv => kv.Key, kv => MiniJson.GetString(MiniJson.GetObject(command, "args")!, kv.Key) ?? "", StringComparer.Ordinal);
        var timeout = TimeSpan.FromMilliseconds(long.TryParse(MiniJson.GetString(command, "timeoutMs"), out var ms) && ms > 0 ? ms : 60000);
        var evidence = Path.Combine(_switch.Folder, "evidence", $"{_runningSeq:D3}-{Safe(id)}-{Safe(_switch.Side)}");
        var context = new StepContext(id, _switch.Side, args, _state, (name, text) =>
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, Safe(name) + ".txt"), text);
        });
        context.RunFolder = _switch.Folder;
        _runningStarted = now;
        var step = InGameTests.Find(id);
        if (step == null || !InGameTests.Runs(step.Side, _switch.Side))
        {
            Result(context, new StepEnd(false, step == null ? $"no step '{id}' is registered in this game" : $"step '{id}' does not run on {_switch.Side}"), now);
            return;
        }
        _log($"test run: step {_runningSeq} {id} started");
        _running = new StepRunner(step.Body, context, now, timeout);
        var outcome = _running.Tick(now);
        if (outcome != null) Finish(outcome, now);
    }

    private void Finish(StepEnd outcome, DateTime now)
    {
        var context = _running!.Context;
        _running = null;
        if (!outcome.Passed && _screenshot != null && _switch.Side != "server")
        {
            var folder = Path.Combine(_switch.Folder, "evidence", $"{_runningSeq:D3}-{Safe(context.StepId)}-{Safe(_switch.Side)}");
            Directory.CreateDirectory(folder);
            try { if (!_screenshot(Path.Combine(folder, "screenshot.bmp"))) context.Log("no screenshot taken"); }
            catch (Exception ex) { context.Log("screenshot failed: " + ex.Message); }
        }
        Result(context, outcome, now);
    }

    private void Result(StepContext context, StepEnd outcome, DateTime now)
    {
        _log($"test run: step {_runningSeq} {context.StepId} {(outcome.Passed ? "passed" : "FAILED: " + outcome.Message)}");
        Append(new Dictionary<string, object?>
        {
            ["op"] = "result",
            ["seq"] = _runningSeq,
            ["step"] = context.StepId,
            ["side"] = _switch.Side,
            ["status"] = outcome.Passed ? "pass" : "fail",
            ["message"] = outcome.Message,
            ["values"] = context.Values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["log"] = context.Lines.Cast<object?>().ToList(),
            ["ms"] = (long)(now - _runningStarted).TotalMilliseconds,
        });
    }

    private void ReadControl()
    {
        var path = Path.Combine(_switch.Folder, "control.jsonl");
        if (!File.Exists(path)) return;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length <= _controlOffset) return;
            stream.Position = _controlOffset;
            var bytes = new byte[stream.Length - _controlOffset];
            var read = stream.Read(bytes, 0, bytes.Length);
            _controlOffset += read;
            _partial.Write(bytes, 0, read);
        }
        // Decode whole lines only: a read can end inside a multi-byte character of a line still being written.
        var buffer = _partial.ToArray();
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline < 0) return;
        _partial.SetLength(0);
        _partial.Write(buffer, lastNewline + 1, buffer.Length - lastNewline - 1);
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, lastNewline).Split('\n'))
            if (line.Trim().Length > 0) _pending.Enqueue(line.Trim());
    }

    private void Append(Dictionary<string, object?> line) =>
        File.AppendAllText(_resultsPath, MiniJson.SerializeCompact(line) + "\n", new UTF8Encoding(false));

    private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
}
