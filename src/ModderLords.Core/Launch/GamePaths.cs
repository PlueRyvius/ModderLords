namespace ModderLords.Core.Launch;

/// <summary>
/// Locations that belong to the player's own Bannerlord install, with no dedicated server anywhere in sight.
/// <see cref="ServerPaths"/> keeps the coop-only half and delegates the Steam lookups here.
/// </summary>
public static class GamePaths
{
    /// <summary>Bannerlord's Steam app id. Workshop mods live under steamapps\workshop\content\261550.</summary>
    public const long BannerlordAppId = 261550;

    public static string ClientBin(string gameRoot) => Path.Combine(gameRoot, "bin", "Win64_Shipping_Client");
    public static string ModulesDir(string gameRoot) => Path.Combine(gameRoot, "Modules");
    public static string WorkshopRoot(string steamLibrary) => Path.Combine(steamLibrary, "steamapps", "workshop", "content", BannerlordAppId.ToString());

    /// <summary>Every Steam library folder on this machine: the default install locations on each fixed drive,
    /// plus whatever libraryfolders.vdf lists.</summary>
    /// <summary>
    /// Replaces Steam library discovery for the duration of a test. Without it a test scans whatever the machine
    /// running it happens to have installed, so the same test passes here and fails on a build agent — and a
    /// fixture that means to describe three modules quietly describes thirty.
    /// </summary>
    internal static Func<IEnumerable<string>>? SteamLibrariesOverride;

    public static IEnumerable<string> SteamLibraries() => SteamLibrariesOverride is { } o ? o() : DiscoverSteamLibraries();

    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrEmpty(pf)) roots.Add(Path.Combine(pf, "Steam"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            var r = drive.RootDirectory.FullName;
            roots.Add(Path.Combine(r, "Program Files (x86)", "Steam"));
            roots.Add(Path.Combine(r, "Program Files", "Steam"));
            roots.Add(Path.Combine(r, "SteamLibrary"));
            roots.Add(Path.Combine(r, "Steam"));
        }
        foreach (var root in roots)
        {
            if (!Directory.Exists(Path.Combine(root, "steamapps"))) continue;
            if (seen.Add(root)) yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var q = t.IndexOf('"', 6);
                var q2 = t.LastIndexOf('"');
                if (q < 0 || q2 <= q) continue;
                var p = t.Substring(q + 1, q2 - q - 1).Replace("\\\\", "\\");
                if (Directory.Exists(Path.Combine(p, "steamapps")) && seen.Add(p)) yield return p;
            }
        }
    }

    /// <summary>The Bannerlord install, or null when no Steam library has one.</summary>
    public static string? FindGameRoot() => Modules.ModuleCatalog.FindGameRoot(SteamLibraries());

    /// <summary>Documents\Mount and Blade II Bannerlord — saves, Configs\LauncherData.xml, mod logs.</summary>
    public static string UserDataDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
            docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(docs, "Mount and Blade II Bannerlord");
    }
}
