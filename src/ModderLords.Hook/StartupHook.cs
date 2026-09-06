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
    private const string Prefix = "[ModderLords.Hook] ";

    private static readonly object Gate = new object();
    private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string[] _dirs = Array.Empty<string>();
    private static bool _verbose;

    public static void Initialize()
    {
        _dirs = ReadDirs();
        _verbose = Environment.GetEnvironmentVariable(VerboseVariable) == "1";
        // AssemblyResolve only: the load context's Resolving event fires before every AppDomain handler, which would
        // let us pre-empt the engine's and Coop's own resolvers. Here we are a fallback, not a replacement.
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        // The engine's own crash path can exit before the runtime prints the exception; make sure the launcher log has it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Console.Out.WriteLine(Prefix + "UNHANDLED EXCEPTION (engine is about to exit):");
                Console.Out.WriteLine(e.ExceptionObject?.ToString() ?? "(no exception object)");
                Console.Out.Flush();
            }
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
                    Console.Out.WriteLine(Prefix + "first-chance " + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' '));
                    for (int i = 0; i < Math.Min(4, frames.Length); i++) Console.Out.WriteLine(Prefix + "    " + frames[i].Trim());
                }
                catch { }
            };
        }
        Console.WriteLine(Prefix + $"assembly resolver active over {_dirs.Length} search dir(s)");
        if (_verbose) foreach (var d in _dirs) Console.WriteLine(Prefix + "  " + d);
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        => Resolve(new AssemblyName(args.Name), args.RequestingAssembly);

    private static Assembly? Resolve(AssemblyName requested, Assembly? requestingAssembly)
    {
        if (string.IsNullOrEmpty(requested.Name) || requested.Name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            return null;

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
            Console.WriteLine(Prefix + $"resolved {name} from {path}" + (by != null ? $" (for {by})" : ""));
        }
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
