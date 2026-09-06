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

    /// <summary>Every Steam library folder on this machine.</summary>
    public static IEnumerable<string> SteamLibraries() => ServerPaths.SteamLibraries();

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
