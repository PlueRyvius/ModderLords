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
/// Locations of the official Bannerlord Coop dedicated server package and the data directories the server owns.
/// Nothing here is written; it only resolves and validates paths.
/// </summary>
public sealed record ServerPaths(string DedicatedServerRoot, string DataDir, string CoopDataDir)
{
    public const long CoopWorkshopItemId = 3770450698;

    public string EngineRoot => Path.Combine(DedicatedServerRoot, "engine");
    public string ServerBin => Path.Combine(EngineRoot, "bin", "Win64_Shipping_Server");
    public string ModulesRoot => Path.Combine(EngineRoot, "Modules");
    public string DotnetExe => Path.Combine(EngineRoot, "dotnet", "dotnet.exe");
    public string StarterDll => Path.Combine(ServerBin, "TaleWorlds.Starter.DotNetCore.dll");
    public string OfficialHostExe => Path.Combine(DedicatedServerRoot, "BannerlordCoopServer.exe");
    public string ServerConfigPath => Path.Combine(DataDir, "server-config.json");
    public string ModConfigPath => Path.Combine(CoopDataDir, "mod-config.json");
    public string SavesDir => Path.Combine(DataDir, "Game Saves");
    public string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>
    /// Where the real game keeps a player's saves: Documents\Mount and Blade II Bannerlord\Game Saves. The server
    /// writes to <see cref="SavesDir"/> under CoopData instead, and nothing copies between the two, which is why a
    /// world made by a modded client cannot be hosted without an explicit import.
    /// </summary>
    public static string ClientSavesDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
            docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(docs, "Mount and Blade II Bannerlord", "Game Saves");
    }

    /// <summary>Default data dir used by the official host: Documents\Mount and Blade II Bannerlord\CoopData.</summary>
    public static string DefaultCoopDataDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
            docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(docs, "Mount and Blade II Bannerlord", "CoopData");
    }

    public static ServerPaths Create(string dedicatedServerRoot, string? dataDir = null, string? coopDataDir = null)
    {
        coopDataDir ??= DefaultCoopDataDir();
        dataDir ??= Path.Combine(coopDataDir, "DedicatedServer");
        return new ServerPaths(Path.GetFullPath(dedicatedServerRoot), Path.GetFullPath(dataDir), Path.GetFullPath(coopDataDir));
    }

    /// <summary>Looks for the workshop copy of the Coop item across every Steam library on this machine.</summary>
    public static IEnumerable<string> FindWorkshopDedicatedServerRoots()
    {
        foreach (var lib in SteamLibraries())
        {
            var candidate = Path.Combine(lib, "steamapps", "workshop", "content", "261550", CoopWorkshopItemId.ToString(), "DedicatedServer");
            if (File.Exists(Path.Combine(candidate, "BannerlordCoopServer.exe")))
                yield return candidate;
        }
    }

    /// <summary>Kept as a forwarder so existing callers on the server side read the same as they did.</summary>
    public static IEnumerable<string> SteamLibraries() => GamePaths.SteamLibraries();

    public IEnumerable<string> Validate()
    {
        if (!File.Exists(OfficialHostExe)) yield return $"Missing official host: {OfficialHostExe}";
        if (!File.Exists(DotnetExe)) yield return $"Missing bundled .NET: {DotnetExe}";
        if (!File.Exists(StarterDll)) yield return $"Missing engine starter: {StarterDll}";
        foreach (var m in new[] { "Native", "SandBoxCore", "SandBox", "Coop", "DedicatedServer.Windows" })
            if (!File.Exists(Path.Combine(ModulesRoot, m, "SubModule.xml"))) yield return $"Missing stock module: {m}";
    }
}
