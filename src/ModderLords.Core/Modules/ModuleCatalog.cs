using ModderLords.Core.Profiles;
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

    /// <summary>
    /// The manifest names a version the game cannot parse. Bannerlord versions are a prefix letter and then
    /// numbers only - <c>v1.2.3</c>, <c>e1.4.6</c> - so something like <c>v0.9.30a</c> is rejected and read as
    /// alpha 0.0.0 by everything that reads it, including Coop's ModuleValidator, which matches community
    /// modules on id AND version in both directions. Worth saying out loud rather than displaying a version the
    /// mod does not have.
    /// </summary>
    public bool HasUnparsableVersion => Info.Version == ApplicationVersion.Empty
                                        && !string.Equals(Version, ApplicationVersion.Empty.ToString(), StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// How far below a scan root a module may be buried. Mods are increasingly shipped as a <i>package</i> - a
    /// container folder holding <c>Client/&lt;Id&gt;</c> and <c>Server/&lt;Id&gt;</c> - and a person drops the whole
    /// thing into Modules exactly as downloaded. At one level deep that package was invisible: the container has no
    /// SubModule.xml, so both halves were skipped and nothing said why (COOP Family 1.4, 2026-09-19).
    ///
    /// Three is container -> Client|Server -> Id with a level to spare. It is a bound, not a target: a well-formed
    /// install never recurses at all, because every real module folder stops the walk immediately.
    /// </summary>
    private const int MaxContainerDepth = 3;

    private static void ScanRoot(string root, ModuleSourceKind kind, List<DiscoveredModule> modules, List<string> problems)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in EnumerateOrEmpty(root))
            ScanDir(dir, kind, modules, problems, depth: 0);
    }

    private static void ScanDir(string dir, ModuleSourceKind kind, List<DiscoveredModule> modules, List<string> problems, int depth)
    {
        if (IsOurs(dir)) return;
        var isLink = IsReparsePoint(dir);

        // Junctions inside the server's engine\Modules are our own overlay entries, not stock modules. Deliberately
        // still scoped to ServerStock: a junction that IS a module folder is how the isolated client view exposes a
        // chosen copy (ClientModuleView), so skipping links outright would make that view discover nothing.
        if (kind == ModuleSourceKind.ServerStock && isLink) return;

        // A folder with a manifest IS the module. Never descend into it: mods legitimately carry nested folders that
        // contain manifests of their own (TAOM.Dependencies), and treating those as modules invents entries the game
        // will never load. This is also what keeps the walk cheap - the common case stops here, at depth 0.
        if (File.Exists(Path.Combine(dir, "SubModule.xml")))
        {
            var parsed = TryParse(dir, kind, out var problem);
            if (parsed is not null) modules.Add(parsed);
            else if (problem is not null) problems.Add(problem);
            return;
        }

        // Recursion is the new part, so this is the new hazard: descending THROUGH a link can re-enter a tree that is
        // already being scanned (or loop). A link that turned out to be a module was handled above; one that is not a
        // module has nothing we need badly enough to risk walking it.
        if (isLink) return;

        if (depth >= MaxContainerDepth) return;
        foreach (var child in EnumerateOrEmpty(dir)) ScanDir(child, kind, modules, problems, depth + 1);
    }

    /// <summary>
    /// Per-directory, not per-root: one unreadable folder - a permissions hole, an antivirus quarantine, a dangling
    /// junction - must cost that folder and nothing else. Letting it throw would abandon every later root as well.
    /// </summary>
    private static IEnumerable<string> EnumerateOrEmpty(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool IsReparsePoint(string dir)
    {
        try { return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; }   // cannot tell = do not walk into it
    }

    /// <summary>
    /// Anything under our own data directory: overlays, shadow copies, module backups, the isolated client view.
    /// Those hold real SubModule.xml files, so a custom root pointing at or above them would otherwise discover
    /// ModderLords' own working copies as if they were installed mods. Derived from <see cref="ProfileStore.RootDir"/>
    /// rather than matched by folder name, so renaming any of those folders cannot silently defeat this.
    /// </summary>
    private static bool IsOurs(string dir)
    {
        try
        {
            var ours = Path.GetFullPath(ProfileStore.RootDir);
            var full = Path.GetFullPath(dir);
            return full.Equals(ours, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(ours.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The version to show for a module. Normally that is the parsed one, but BUTR's parser accepts only
    /// <c>&lt;prefix&gt;&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;[.&lt;revision&gt;]</c> and silently yields
    /// alpha 0.0.0 for anything else - so a manifest reading <c>v0.9.30a</c> was displayed as <c>a0.0.0</c>, a
    /// version the mod does not have and which sorts as older than everything.
    ///
    /// When the parse produced nothing and the manifest does say something, the manifest wins: it is what the
    /// author wrote and what the TaleWorlds launcher shows. <see cref="DiscoveredModule.Info"/> keeps the parsed
    /// value, so dependency resolution is unaffected.
    /// </summary>
    private static string DisplayVersion(XmlDocument doc, ModuleInfoExtended info)
    {
        var parsed = info.Version.ToString();
        if (info.Version != ApplicationVersion.Empty) return parsed;
        var raw = doc.SelectSingleNode("//Version")?.Attributes?["value"]?.Value?.Trim();
        return string.IsNullOrWhiteSpace(raw) ? parsed : raw!;
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
            return new DiscoveredModule(info.Id, DisplayVersion(doc, info), Path.GetFullPath(folder), kind, info);
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
