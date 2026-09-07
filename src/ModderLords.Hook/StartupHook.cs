using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Loaded into the headless engine through DOTNET_STARTUP_HOOKS (the type must be named StartupHook in the global
/// namespace with a static Initialize()). Its only job: when the engine cannot find a managed assembly, look for it in
/// the directories listed in MODDERLORDS_SEARCH_DIRS (';' separated). The engine itself only probes its own bin
/// folder, so a mod's helper DLLs and the client-only view assemblies mods reference would otherwise be unresolvable.
/// No game code is patched here.
/// </summary>
internal static class StartupHook
{
    private const string SearchDirsVariable = "MODDERLORDS_SEARCH_DIRS";
    private const string VerboseVariable = "MODDERLORDS_HOOK_VERBOSE";
    private const string SidecarVariable = "MODDERLORDS_HOOK_LOG";
    private const string Prefix = "[ModderLords.Hook] ";

    private static readonly object Gate = new object();
    private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string[] _dirs = Array.Empty<string>();
    private static bool _verbose;
    private static string? _sidecar;

    // Breadcrumbs. The engine can die without unwinding (a native abort inside the UI stack leaves no managed
    // exception), and stdout is then lost with it. These are the last things we saw, written out on the way down.
    private static string _lastRequested = "(none)";
    private static string _lastRequester = "(none)";
    private static string _lastResolved = "(none)";

    public static void Initialize()
    {
        _dirs = ReadDirs();
        _verbose = Environment.GetEnvironmentVariable(VerboseVariable) == "1";
        _sidecar = OpenSidecar();
        // AssemblyResolve only: the load context's Resolving event fires before every AppDomain handler, which would
        // let us pre-empt the engine's and Coop's own resolvers. Here we are a fallback, not a replacement.
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        // The engine's own crash path can exit before the runtime prints the exception; make sure the launcher log has it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Write("UNHANDLED EXCEPTION (engine is about to exit):");
                Write(e.ExceptionObject?.ToString() ?? "(no exception object)");
                Write(Breadcrumbs());
            }
            catch { }
        };
        // A silent death (no managed exception) still runs this on a normal runtime shutdown; when it does not, the
        // sidecar file already holds every line written up to the crash.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Write("process exiting with code " + Environment.ExitCode + "; " + Breadcrumbs()); }
            catch { }
        };
        if (Environment.GetEnvironmentVariable("MODDERLORDS_HOOK_FIRSTCHANCE") == "1")
        {
            // Diagnostic firehose: every thrown exception, even handled ones. Only for hunting a silent engine death.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    var ex = e.Exception;
                    var frames = (ex.StackTrace ?? "").Split('\n');
                    Write("first-chance " + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' '));
                    for (int i = 0; i < Math.Min(4, frames.Length); i++) Write("    " + frames[i].Trim());
                }
                catch { }
            };
        }
        Write($"assembly resolver active over {_dirs.Length} search dir(s)");
        if (_sidecar != null) Write("sidecar log: " + _sidecar);
        if (_verbose) foreach (var d in _dirs) Write("  " + d);
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        => Resolve(new AssemblyName(args.Name), args.RequestingAssembly);

    private static Assembly? Resolve(AssemblyName requested, Assembly? requestingAssembly)
    {
        if (string.IsNullOrEmpty(requested.Name) || requested.Name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            return null;

        _lastRequested = requested.Name!;
        _lastRequester = requestingAssembly?.GetName().Name ?? "(unknown)";

        // Already loaded under this name? Hand it back instead of loading a second copy.
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => NameMatches(requested, a.GetName()));
        if (loaded != null) return loaded;

        var candidates = new List<string>();
        var requesterDir = SafeDirectory(requestingAssembly);
        if (requesterDir != null) candidates.Add(requesterDir);
        candidates.AddRange(_dirs);

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, requested.Name + ".dll");
            if (!File.Exists(path)) continue;
            try
            {
                var found = AssemblyName.GetAssemblyName(path);
                if (!NameMatches(requested, found)) continue;
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                _lastResolved = requested.Name!;
                Announce(requested.Name!, path, requestingAssembly);
                return asm;
            }
            catch (BadImageFormatException) { }
            catch (FileLoadException)
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => NameMatches(requested, a.GetName()));
                if (loaded != null) return loaded;
            }
        }
        return null;
    }

    private static bool NameMatches(AssemblyName requested, AssemblyName candidate)
    {
        if (!string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)) return false;
        // Game and mod assemblies are unsigned and mostly 1.0.0.0; a same-name candidate is the best we can do.
        // Only refuse when both sides carry a public key token and they differ.
        var rt = requested.GetPublicKeyToken();
        var ct = candidate.GetPublicKeyToken();
        if (rt != null && rt.Length > 0 && ct != null && ct.Length > 0 && !rt.SequenceEqual(ct)) return false;
        return true;
    }

    private static void Announce(string name, string path, Assembly? requester)
    {
        lock (Gate)
        {
            if (!Announced.Add(name)) return;
            var by = requester?.GetName().Name;
            Write($"resolved {name} from {path}" + (by != null ? $" (for {by})" : ""));
        }
    }

    /// <summary>
    /// One line to stdout (prefixed, flushed) and, when the launcher gave us a path, to a sidecar file. Flushing
    /// matters: the engine's UI stack can abort the process without draining the redirected pipe, which is how a
    /// crash ends up with a truncated launcher log and no cause in it.
    /// </summary>
    private static void Write(string line)
    {
        var text = Prefix + line;
        try { Console.Out.WriteLine(text); Console.Out.Flush(); } catch { }
        if (_sidecar == null) return;
        try
        {
            lock (Gate) File.AppendAllText(_sidecar, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + System.Environment.NewLine);
        }
        catch { }
    }

    private static string Breadcrumbs() =>
        $"last resolve request: {_lastRequested} (for {_lastRequester}); last assembly actually loaded: {_lastResolved}";

    private static string? OpenSidecar()
    {
        var path = Environment.GetEnvironmentVariable(SidecarVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.AppendAllText(full, $"--- ModderLords hook attached {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {System.Environment.ProcessId}) ---{System.Environment.NewLine}");
            return full;
        }
        catch { return null; }
    }

    private static string? SafeDirectory(Assembly? asm)
    {
        try
        {
            var loc = asm?.Location;
            return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
        }
        catch { return null; }
    }

    private static string[] ReadDirs()
    {
        var raw = Environment.GetEnvironmentVariable(SearchDirsVariable) ?? string.Empty;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => { try { return Path.GetFullPath(s); } catch { return string.Empty; } })
            .Where(s => s.Length > 0 && Directory.Exists(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
