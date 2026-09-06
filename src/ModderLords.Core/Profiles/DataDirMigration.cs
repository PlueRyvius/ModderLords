using System;
using System.IO;

namespace ModderLords.Core.Profiles;

/// <summary>
/// The launcher's data directory was <c>%LOCALAPPDATA%\ModularCoop</c> before the rename to ModderLords.
/// Anyone upgrading has their profiles, local compat records and settings caches sitting in the old folder,
/// so on first run we copy them across.
///
/// Copy, never move: if this build turns out to be broken the old launcher must still find its own data.
/// Only the durable state travels — <c>overlay\</c> holds junctions bound to their old paths and <c>logs\</c>
/// and <c>perf\</c> are regenerated, so all three are left behind deliberately.
/// </summary>
public static class DataDirMigration
{
    public const string LegacyFolderName = "ModularCoop";

    /// <summary>Written into the new root once the copy has run, so a user who deletes a profile
    /// does not get it resurrected on the next launch.</summary>
    public const string MarkerFileName = "migrated-from-modularcoop";

    private static readonly string[] Subdirectories = ["profiles", "cache"];
    private static readonly string[] Files = ["compat-db.local.json"];

    public static string LegacyRootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

    /// <summary>Copies legacy data into <paramref name="newRoot"/> if it has not been done before.
    /// Returns the number of files copied; 0 means there was nothing to do.</summary>
    public static int RunIfNeeded(string? newRoot = null, string? legacyRoot = null)
    {
        newRoot ??= ProfileStore.RootDir;
        legacyRoot ??= LegacyRootDir;

        var marker = Path.Combine(newRoot, MarkerFileName);
        if (File.Exists(marker)) return 0;
        if (!Directory.Exists(legacyRoot)) return 0;

        // A new root that already holds profiles belongs to a build that ran after the rename; leave it alone
        // and just stamp the marker so we never look again.
        var alreadyInUse = Directory.Exists(Path.Combine(newRoot, "profiles"))
                           && Directory.EnumerateFiles(Path.Combine(newRoot, "profiles"), "*.json").GetEnumerator().MoveNext();

        var copied = 0;
        if (!alreadyInUse)
        {
            foreach (var sub in Subdirectories)
                copied += CopyTree(Path.Combine(legacyRoot, sub), Path.Combine(newRoot, sub));

            foreach (var file in Files)
            {
                var src = Path.Combine(legacyRoot, file);
                if (!File.Exists(src)) continue;
                Directory.CreateDirectory(newRoot);
                File.Copy(src, Path.Combine(newRoot, file), overwrite: false);
                copied++;
            }
        }

        Directory.CreateDirectory(newRoot);
        File.WriteAllText(marker, $"Copied from {legacyRoot} on {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({copied} file(s)).{Environment.NewLine}"
                                  + $"The old folder was left in place and can be deleted once this build is working.{Environment.NewLine}");
        return copied;
    }

    private static int CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return 0;
        Directory.CreateDirectory(destination);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            copied++;
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            copied += CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
        return copied;
    }
}
