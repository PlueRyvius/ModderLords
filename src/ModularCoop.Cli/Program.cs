using ModularCoop.Core.Launch;
using ModularCoop.Core.Logs;
using ModularCoop.Core.Saves;

// Phase 0 spike: launch the pristine engine with the stock module set and stream classified output.
// usage: ModularCoop.Cli launch [--root <DedicatedServer>] [--modules A,B,C] [--save NAME] [--port 7210] [--region EU] [--dry-run] [--quiet-engine] [--stop-after SECONDS]

var opts = ParseArgs(args);
if (!opts.TryGetValue("cmd", out var cmd) || cmd != "launch")
{
    Console.WriteLine("usage: launch [--root <DedicatedServer>] [--modules A,B,C] [--save NAME] [--port N] [--region EU] [--dry-run] [--quiet-engine] [--stop-after SECONDS]");
    return 1;
}

var root = opts.GetValueOrDefault("root") ?? ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault();
if (root is null) { Console.Error.WriteLine("No DedicatedServer folder found; pass --root."); return 2; }
var paths = ServerPaths.Create(root);
var problems = paths.Validate().ToList();
foreach (var p in problems) Console.Error.WriteLine("[ModularCoop] " + p);
if (problems.Count > 0) return 2;

var modules = opts.TryGetValue("modules", out var m) && !string.IsNullOrWhiteSpace(m)
    ? m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : LaunchPlan.StockModuleOrder;

var plan = new LaunchPlan
{
    Paths = paths,
    ModuleIds = modules,
    EnginePort = int.TryParse(opts.GetValueOrDefault("port"), out var port) ? port : 7210,
    Region = opts.GetValueOrDefault("region") ?? "EU",
    SaveName = opts.GetValueOrDefault("save"),
};

Console.WriteLine("[ModularCoop] launch plan");
Console.Write(plan.Describe());
if (opts.ContainsKey("dry-run")) return 0;

if (plan.SaveName is { } saveName)
{
    var prep = SavePreparer.EnsureExists(paths, saveName);
    Console.WriteLine(prep.CreatedFromTemplate
        ? $"[ModularCoop] save '{saveName}' did not exist; created a fresh world from {prep.TemplateUsed}"
        : $"[ModularCoop] hosting existing save {prep.SavePath}");
}

var quietEngine = opts.ContainsKey("quiet-engine");
var stopAfter = int.TryParse(opts.GetValueOrDefault("stop-after"), out var s) ? s : 0;
var logPath = Path.Combine(Path.GetTempPath(), $"modularcoop-launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
using var log = new StreamWriter(logPath) { AutoFlush = true };
Console.WriteLine($"[ModularCoop] full output -> {logPath}");

using var engine = EngineProcess.Start(plan);
Console.WriteLine($"[ModularCoop] engine pid {engine.ProcessId}. Type a command and Enter to send it; 'quit' stops the server.");
var serving = new TaskCompletionSource();
engine.LineReceived += line =>
{
    var c = LogClassifier.Classify(line.Text);
    log.WriteLine($"{line.At:HH:mm:ss.fff} {line.Stream,-6} {c.Category,-10} {line.Text}");
    if (line.Text.Contains("SERVING", StringComparison.Ordinal)) serving.TrySetResult();
    if (quietEngine && c.Category is LogCategory.Engine) return;
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = c.Category switch
    {
        LogCategory.Error => ConsoleColor.Red,
        LogCategory.Warning => ConsoleColor.Yellow,
        LogCategory.Milestone => ConsoleColor.Green,
        LogCategory.ModuleLoad => ConsoleColor.Cyan,
        LogCategory.Server => ConsoleColor.White,
        LogCategory.Coop => ConsoleColor.Magenta,
        LogCategory.Tool => ConsoleColor.Green,
        _ => ConsoleColor.DarkGray,
    };
    Console.WriteLine($"{c.Category,-10} {line.Text}");
    Console.ForegroundColor = prev;
};

Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = engine.StopAsync(TimeSpan.FromSeconds(15)); };

if (stopAfter > 0)
{
    _ = Task.Run(async () =>
    {
        await Task.WhenAny(serving.Task, Task.Delay(TimeSpan.FromSeconds(stopAfter)));
        await Task.Delay(TimeSpan.FromSeconds(5));
        Console.WriteLine("[ModularCoop] auto-stop: sending 'stop' over stdin");
        await engine.StopAsync(TimeSpan.FromSeconds(30));
    });
}
else
{
    _ = Task.Run(async () =>
    {
        while (engine.IsRunning)
        {
            var input = Console.ReadLine();
            if (input is null) break;
            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) { await engine.StopAsync(TimeSpan.FromSeconds(15)); break; }
            try { await engine.SendCommandAsync(input); } catch (Exception ex) { Console.WriteLine("[ModularCoop] send failed: " + ex.Message); }
        }
    });
}

var code = await engine.Exited;
var uptime = DateTimeOffset.Now - engine.StartedAt;
Console.WriteLine($"[ModularCoop] engine exit code {code} after {uptime:hh\\:mm\\:ss}. {ExitCodeExplainer.Explain(code)}");
log.WriteLine($"[ModularCoop] exit {code}: {ExitCodeExplainer.Explain(code)}");
return code;

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--"))
        {
            var key = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i]; else d[key] = "true";
        }
        else if (!d.ContainsKey("cmd")) d["cmd"] = a[i];
    }
    return d;
}
