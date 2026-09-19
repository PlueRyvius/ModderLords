
using ModderLords.Core.Compat;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>
/// Environment for ModderLords.Hook: DOTNET_STARTUP_HOOKS points at the hook DLL and MODDERLORDS_SEARCH_DIRS lists
/// where the engine may find assemblies it cannot resolve itself. Order matters: a mod's own server bin (as the engine
/// sees it through the junction) first, then other mods' bins, then the game's client bin as a last resort for the
/// client-only view assemblies mods reference but never execute on a headless server.
/// </summary>
public static class HookSetup
{
    public const string HookFileName = "ModderLords.Hook.dll";
    public const string SearchDirsVariable = "MODDERLORDS_SEARCH_DIRS";

    /// <summary>
    /// The real game's Microsoft.WindowsDesktop.App folder, from which the hook may supply a SHORT ALLOW-LIST of
    /// assemblies the server's own runtime lacks. Deliberately not just another search dir: that folder also holds
    /// System.Windows.Forms and the whole of WPF, and supplying those would be actively worse than the crash they
    /// prevent - MessageBox.Show blocks its thread until someone clicks a dialog, so a mod whose error handler pops
    /// one would turn a loud crash into a silent hang on a server nobody is looking at. The hook decides what it is
    /// willing to take from here; this only says where "here" is.
    /// </summary>
    public const string DesktopDirVariable = "MODDERLORDS_DESKTOP_DIR";

    /// <summary>The game's desktop-framework folder, or null. Version-checked by the hook, not here.</summary>
    public static string? DesktopFrameworkDir(string? gameRoot)
    {
        if (gameRoot is null) return null;
        var dir = Path.Combine(gameRoot, "bin", "Win64_Shipping_Client", "Microsoft.WindowsDesktop.App");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Where the hook mirrors its own output. Survives a crash that takes the redirected stdout pipe with it.</summary>
    public const string SidecarVariable = "MODDERLORDS_HOOK_LOG";

    /// <summary>Sidecar path for one launch, alongside the launcher's own logs.</summary>
    public static string SidecarPathFor(DateTime startedAt) =>
        Path.Combine(ProfileStore.RootDir, "logs", $"hook-{startedAt:yyyyMMdd-HHmmss}.log");

    /// <summary>Where the release keeps the parts that cannot live inside the single-file exe.</summary>
    public const string BinFolder = "bin";

    /// <summary>
    /// Path of the hook DLL. The release puts it under bin\ to keep the folder people unzip readable; a dev build
    /// leaves it next to the exe, so both are checked. It has to stay a real file either way: the engine loads it
    /// through DOTNET_STARTUP_HOOKS, in a different process, so it can never be bundled into our exe.
    /// </summary>
    public static string? LocateHook() => LocateHookIn(LaunchSession.BundledRoot ?? AppContext.BaseDirectory);

    /// <summary>The same lookup against a given folder, so the order of preference can be tested.</summary>
    public static string? LocateHookIn(string baseDir)
    {
        foreach (var candidate in new[] { Path.Combine(baseDir, BinFolder, HookFileName), Path.Combine(baseDir, HookFileName) })
            if (File.Exists(candidate)) return candidate;
        return null;
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

    public static IReadOnlyDictionary<string, string> Environment(string hookDllPath, IEnumerable<string> searchDirs, bool verbose = false, string? sidecarPath = null, string? desktopDir = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = Path.GetFullPath(hookDllPath),
            [SearchDirsVariable] = string.Join(";", searchDirs),
        };
        if (!string.IsNullOrWhiteSpace(desktopDir)) env[DesktopDirVariable] = desktopDir!;
        if (verbose) env["MODDERLORDS_HOOK_VERBOSE"] = "1";
        if (!string.IsNullOrWhiteSpace(sidecarPath)) env[SidecarVariable] = sidecarPath!;
        return env;
    }
}
