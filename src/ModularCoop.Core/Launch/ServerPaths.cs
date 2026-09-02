namespace ModularCoop.Core.Launch;

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

    public static IEnumerable<string> SteamLibraries()
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

    public IEnumerable<string> Validate()
    {
        if (!File.Exists(OfficialHostExe)) yield return $"Missing official host: {OfficialHostExe}";
        if (!File.Exists(DotnetExe)) yield return $"Missing bundled .NET: {DotnetExe}";
        if (!File.Exists(StarterDll)) yield return $"Missing engine starter: {StarterDll}";
        foreach (var m in new[] { "Native", "SandBoxCore", "SandBox", "Coop", "DedicatedServer.Windows" })
            if (!File.Exists(Path.Combine(ModulesRoot, m, "SubModule.xml"))) yield return $"Missing stock module: {m}";
    }
}
