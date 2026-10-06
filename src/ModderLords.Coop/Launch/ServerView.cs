using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Launch;

/// <summary>
/// A private view of the dedicated server for installs on a drive that cannot hold junctions (exFAT, FAT32, a
/// network share). Mods reach the engine as junctions inside engine\Modules, and a junction is stored on the
/// drive that holds the link, so on such a drive not one mod can be linked in. The view is the same folder
/// layout built somewhere that can hold them: every folder of the real server is a junction back to it, and
/// engine\Modules is a real folder with one junction per stock module, into which the overlay then links the
/// mods. Nothing is copied apart from loose files directly under engine\, and the installation is not written to.
/// The game client is launched the same way (<see cref="ModderLords.Core.Launch.ClientModuleView"/>).
/// </summary>
public static class ServerView
{
    /// <summary>Set to 1 to run from a view even where the server's own drive could hold the junctions.</summary>
    public const string ForceVariable = "MODDERLORDS_SERVER_VIEW";

    /// <summary>The paths to launch with: unchanged, or pointing at a view when the server's drive needs one.</summary>
    public static ServerPaths For(ServerPaths paths) => For(paths, ProfileStore.ServerViewsDir,
        Environment.GetEnvironmentVariable(ForceVariable) == "1");

    /// <summary>As above, with the views folder and the override supplied (tests).</summary>
    public static ServerPaths For(ServerPaths paths, string viewsDir, bool force)
    {
        if (!force && Junction.SupportedAt(paths.StockModulesRoot)) return paths;
        return paths with { ViewRoot = Path.Combine(Path.GetFullPath(viewsDir), Key(paths.DedicatedServerRoot)) };
    }

    /// <summary>Why this launch runs from a view, for the launch console. Null when it does not.</summary>
    public static string? Describe(ServerPaths paths)
    {
        if (paths.ViewRoot is not { } view) return null;
        var why = Junction.SupportedAt(paths.StockModulesRoot)
            ? $"{ForceVariable} is set"
            : $"the dedicated server is on a drive that cannot hold links ({DriveLabel(paths.DedicatedServerRoot)})";
        return $"{why}, so the server runs from a private view at {view}. Nothing is copied and the installation is not changed.";
    }

    /// <summary>Creates or refreshes the view. Does nothing for paths that have none.</summary>
    [SupportedOSPlatform("windows")]
    public static void Build(ServerPaths paths)
    {
        if (paths.ViewRoot is not { } view) return;
        if (!Junction.SupportedAt(view))
            throw new InvalidOperationException(
                $"Mods cannot be linked into the dedicated server. It is on a drive that cannot hold links " +
                $"({DriveLabel(paths.DedicatedServerRoot)}), and neither can the launcher's own data folder " +
                $"({DriveLabel(view)}). Move the DedicatedServer folder to an NTFS drive and set it under Folders.");

        var viewEngine = Path.Combine(view, "engine");
        var viewModules = Path.Combine(viewEngine, "Modules");
        Directory.CreateDirectory(viewModules);

        SyncLinks(paths.DedicatedServerRoot, view, skip: "engine");
        SyncLinks(paths.StockEngineRoot, viewEngine, skip: "Modules");
        SyncLinks(paths.StockModulesRoot, viewModules, skip: null);

        // The engine's base folder is engine\, so a loose file there has to be a real file in the view.
        foreach (var file in Directory.EnumerateFiles(paths.StockEngineRoot))
        {
            var source = new FileInfo(file);
            var dest = new FileInfo(Path.Combine(viewEngine, source.Name));
            if (dest.Exists && dest.Length == source.Length && dest.LastWriteTimeUtc == source.LastWriteTimeUtc) continue;
            File.Copy(source.FullName, dest.FullName, overwrite: true);
            File.SetLastWriteTimeUtc(dest.FullName, source.LastWriteTimeUtc);
        }
    }

    /// <summary>
    /// One junction in <paramref name="viewDir"/> per real folder of <paramref name="realDir"/>, and none left over
    /// from a folder that has since gone. Junctions that point anywhere else are the overlay's and are left alone.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void SyncLinks(string realDir, string viewDir, string? skip)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(realDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals(skip, StringComparison.OrdinalIgnoreCase)) continue;
            // A junction in the installation is an overlay entry from a launch made without the view.
            if (Junction.IsJunction(dir)) continue;
            Junction.Create(Path.Combine(viewDir, name), dir);
            wanted.Add(name);
        }

        var prefix = Path.GetFullPath(realDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var dir in Directory.EnumerateDirectories(viewDir))
        {
            if (wanted.Contains(Path.GetFileName(dir))) continue;
            if (Junction.Target(dir) is { } target && target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Junction.Remove(dir);
        }
    }

    /// <summary>One view per server folder, so two profiles hosting from the same install share it as they share the install.</summary>
    private static string Key(string serverRoot)
    {
        var normalised = Path.GetFullPath(serverRoot).TrimEnd('\\', '/').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..12].ToLowerInvariant();
    }

    private static string DriveLabel(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network ? $"{root} network drive" : $"{root} {drive.DriveFormat}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return root;
        }
    }
}
