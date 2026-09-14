using ModderLords.Core.Launch;
using ModderLords.Core.Modules;

namespace ModderLords.Core.Tests;

/// <summary>Modules installed on this machine (game Modules first, then Workshop), by id; null when the game is absent.</summary>
internal static class InstalledMods
{
    public static Dictionary<string, DiscoveredModule>? Find()
    {
        var libs = GamePaths.SteamLibraries().ToList();
        var game = ModuleCatalog.FindGameRoot(libs);
        if (game is null) return null;
        var mods = new Dictionary<string, DiscoveredModule>(StringComparer.OrdinalIgnoreCase);
        void Collect(string root, ModuleSourceKind kind)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (ModuleCatalog.TryParse(dir, kind, out _) is { } m) mods.TryAdd(m.Id, m);
        }
        Collect(Path.Combine(game, "Modules"), ModuleSourceKind.GameModules);
        foreach (var lib in libs) Collect(GamePaths.WorkshopRoot(lib), ModuleSourceKind.Workshop);
        return mods;
    }
}
