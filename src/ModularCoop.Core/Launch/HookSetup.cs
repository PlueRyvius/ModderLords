using ModularCoop.Core.Overlay;

namespace ModularCoop.Core.Launch;

/// <summary>
/// Environment for ModularCoop.Hook: DOTNET_STARTUP_HOOKS points at the hook DLL and MODULARCOOP_SEARCH_DIRS lists
/// where the engine may find assemblies it cannot resolve itself. Order matters: a mod's own server bin (as the engine
/// sees it through the junction) first, then other mods' bins, then the game's client bin as a last resort for the
/// client-only view assemblies mods reference but never execute on a headless server.
/// </summary>
public static class HookSetup
{
    public const string HookFileName = "ModularCoop.Hook.dll";
    public const string SearchDirsVariable = "MODULARCOOP_SEARCH_DIRS";

    /// <summary>Path of the hook DLL shipped next to the running application, or null when it is not there.</summary>
    public static string? LocateHook()
    {
        var baseDir = AppContext.BaseDirectory;
        var p = Path.Combine(baseDir, HookFileName);
        return File.Exists(p) ? p : null;
    }

    public static IReadOnlyList<string> SearchDirs(ServerPaths paths, IEnumerable<OverlayEntry> entries, string? gameRoot)
    {
        var dirs = new List<string>();
        // Stock server bins first: shared libraries (Serilog, Harmony, Newtonsoft) must come from Coop's own copies,
        // never from an older one a mod happens to bundle.
        foreach (var stock in new[] { "Coop", "DedicatedServer.Windows", "SandBox" })
            dirs.Add(Path.Combine(paths.ModulesRoot, stock, "bin", "Win64_Shipping_Server"));
        foreach (var e in entries)
        {
            // What the engine loads from (junction path), then the real client bin for anything not mirrored.
            dirs.Add(Path.Combine(e.EngineModulePath, "bin", "Win64_Shipping_Server"));
            if (e.Selection.Module.HasClientBin) dirs.Add(e.Selection.Module.ClientBin);
            var desktop = Path.Combine(e.Selection.Module.FolderPath, "bin", "Gaming.Desktop.x64_Shipping_Client");
            if (Directory.Exists(desktop)) dirs.Add(desktop);
        }
        if (gameRoot is not null)
        {
            dirs.Add(Path.Combine(gameRoot, "bin", "Win64_Shipping_Client"));
            foreach (var official in new[] { "StoryMode", "CustomBattle", "SandBox", "SandBoxCore", "Native", "BirthAndDeath" })
            {
                var bin = Path.Combine(gameRoot, "Modules", official, "bin", "Win64_Shipping_Client");
                if (Directory.Exists(bin)) dirs.Add(bin);
            }
        }
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyDictionary<string, string> Environment(string hookDllPath, IEnumerable<string> searchDirs, bool verbose = false)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = Path.GetFullPath(hookDllPath),
            [SearchDirsVariable] = string.Join(";", searchDirs),
        };
        if (verbose) env["MODULARCOOP_HOOK_VERBOSE"] = "1";
        return env;
    }
}
