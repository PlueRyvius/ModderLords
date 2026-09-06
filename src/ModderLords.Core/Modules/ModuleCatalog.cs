using System.Xml;
using Bannerlord.ModuleManager;

namespace ModderLords.Core.Modules;

public enum ModuleSourceKind { ServerStock, GameModules, Workshop, Custom }

/// <summary>A module folder found on disk, parsed with BUTR's model (SubModule.xml is the source of truth).</summary>
public sealed record DiscoveredModule(
    string Id,
    string Version,
    string FolderPath,
    ModuleSourceKind Source,
    ModuleInfoExtended Info)
{
    public string FolderName => Path.GetFileName(FolderPath.TrimEnd('\\', '/'));
    public string ServerBin => Path.Combine(FolderPath, "bin", "Win64_Shipping_Server");
    public string ClientBin => Path.Combine(FolderPath, "bin", "Win64_Shipping_Client");
    public bool HasServerBin => Directory.Exists(ServerBin);
    public bool HasClientBin => Directory.Exists(ClientBin);
    public bool IsOfficial => Info.IsOfficial;
    public bool IsStock => Source == ModuleSourceKind.ServerStock;

    /// <summary>True when at least one submodule carries a tag the dedicated server rejects (DedicatedServerType=none / IsNoRenderModeElement=false).</summary>
    public bool HasHeadlessExclusions => Info.SubModules.Any(s =>
        (s.Tags.TryGetValue("DedicatedServerType", out var d) && d.Any(v => v.Equals("none", StringComparison.OrdinalIgnoreCase)))
        || (s.Tags.TryGetValue("IsNoRenderModeElement", out var n) && n.Any(v => v.Equals("false", StringComparison.OrdinalIgnoreCase))));

    public bool HasCode => Info.SubModules.Any(s => !string.IsNullOrEmpty(s.DLLName));
}

/// <summary>Finds modules in the server's own engine\Modules, the game's Modules folder, workshop content, and any custom roots.</summary>
public sealed class ModuleCatalog
{
    public const int BannerlordAppId = 261550;

    public IReadOnlyList<DiscoveredModule> Modules { get; }
    public IReadOnlyList<string> Problems { get; }

    private ModuleCatalog(IReadOnlyList<DiscoveredModule> modules, IReadOnlyList<string> problems)
    {
        Modules = modules; Problems = problems;
    }

    public DiscoveredModule? Find(string id) => Modules.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>All non-stock candidates for an id (the same mod can exist in game Modules and in the workshop).</summary>
    public IEnumerable<DiscoveredModule> Candidates(string id) =>
        Modules.Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !m.IsStock);

    public static ModuleCatalog Scan(string serverModulesRoot, string? gameRoot, IEnumerable<string> steamLibraries, IEnumerable<string> customRoots)
    {
        var modules = new List<DiscoveredModule>();
        var problems = new List<string>();

        ScanRoot(serverModulesRoot, ModuleSourceKind.ServerStock, modules, problems);
        if (gameRoot is not null) ScanRoot(Path.Combine(gameRoot, "Modules"), ModuleSourceKind.GameModules, modules, problems);
        foreach (var lib in steamLibraries)
            ScanRoot(Path.Combine(lib, "steamapps", "workshop", "content", BannerlordAppId.ToString()), ModuleSourceKind.Workshop, modules, problems);
        foreach (var root in customRoots) ScanRoot(root, ModuleSourceKind.Custom, modules, problems);

        return new ModuleCatalog(modules, problems);
    }

    private static void ScanRoot(string root, ModuleSourceKind kind, List<DiscoveredModule> modules, List<string> problems)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // Junctions inside the server's engine\Modules are our own overlay entries, not stock modules.
            if (kind == ModuleSourceKind.ServerStock && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var manifest = Path.Combine(dir, "SubModule.xml");
            if (!File.Exists(manifest)) continue;
            var parsed = TryParse(dir, kind, out var problem);
            if (parsed is not null) modules.Add(parsed);
            else if (problem is not null) problems.Add(problem);
        }
    }

    public static DiscoveredModule? TryParse(string folder, ModuleSourceKind kind, out string? problem)
    {
        problem = null;
        var manifest = Path.Combine(folder, "SubModule.xml");
        try
        {
            var doc = new XmlDocument();
            doc.Load(manifest);
            var info = ModuleInfoExtended.FromXml(doc);
            if (info is null || string.IsNullOrWhiteSpace(info.Id)) { problem = $"{manifest}: no module Id"; return null; }
            return new DiscoveredModule(info.Id, info.Version.ToString(), Path.GetFullPath(folder), kind, info);
        }
        catch (Exception ex)
        {
            problem = $"{manifest}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Locates the Bannerlord client install (needed for game Modules and as a source of mods).</summary>
    public static string? FindGameRoot(IEnumerable<string> steamLibraries)
    {
        foreach (var lib in steamLibraries)
        {
            var candidate = Path.Combine(lib, "steamapps", "common", "Mount & Blade II Bannerlord");
            if (Directory.Exists(Path.Combine(candidate, "Modules", "Native"))) return candidate;
        }
        return null;
    }
}
