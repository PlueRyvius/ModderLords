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

        // Deliberately the real ModuleCatalog.Scan rather than a local directory loop. This helper used to do its own
        // one-level enumerate, which meant tests saw a different set of mods than a launch does - and once discovery
        // learned to look inside package folders, a mod shipped as <package>/Server/<Id> stayed invisible here while
        // being perfectly visible to the app. A test fixture that disagrees with production discovery is worse than
        // no fixture, because it fails in the direction that looks like "not installed".
        var catalog = ModuleCatalog.Scan("", game, libs, []);

        // First wins, matching Scan's root order (game Modules, then Workshop). A package that ships one tree per
        // side yields two entries for one id; either is fine for these tests, since a mod's manifest and DLLs are
        // what they inspect and both trees carry the same ones.
        var mods = new Dictionary<string, DiscoveredModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in catalog.Modules) mods.TryAdd(m.Id, m);
        return mods;
    }
}
