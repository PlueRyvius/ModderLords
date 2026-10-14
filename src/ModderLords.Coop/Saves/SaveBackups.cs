using System.Globalization;
using ModderLords.Coop.Launch;

namespace ModderLords.Coop.Saves;

/// <summary>
/// Copies of the hosted save, taken by the launcher, so a world that goes bad can be rolled back.
///
/// The server writes its autosaves and its stop save over the one file it was started with, so before this there was
/// never an older copy to go back to. Reported 2026-10-13: a campaign ran for 45 hours, joins started failing partway
/// through, and the save written at the next stop crashed the server on every boot. The last good state of a
/// 900-day campaign existed nowhere.
///
/// Each backup is the .sav and Coop's session file beside it (<see cref="SavePreparer.SessionPath"/>): without the
/// session file a restored world has every player's character in it and no player attached to any of them.
///
/// Three kinds, pruned separately so a long session cannot push out the copy taken before it started:
/// <list type="bullet">
/// <item><see cref="Kind.Start"/>: the save as it was when the server was launched.</item>
/// <item><see cref="Kind.Running"/>: taken while the server runs, at most once per <see cref="RunningInterval"/>, and
/// once more after it exits.</item>
/// <item><see cref="Kind.Replaced"/>: the save as it was just before an older version was loaded in its place
/// (<see cref="LoadVersion"/>), so going back is never one-way.</item>
/// </list>
/// A copy is only taken when the save changed since the newest backup, so a server that crashes on every boot does
/// not rotate the good copies out with copies of the broken one.
///
/// Backups live under the server's data folder (ModderLords\save-backups\&lt;save&gt;), not in Game Saves, so the
/// Saves tab and the server never mistake one for a world to host. The Saves tab lists them under their save, next to
/// the two older copies Coop itself keeps beside it (&lt;save&gt;.backup1/.backup2), and <see cref="LoadVersion"/> puts
/// a chosen one in the save's place before a launch.
/// </summary>
public static class SaveBackups
{
    public enum Kind { Start, Running, Replaced }

    public const int KeepStart = 5;
    public const int KeepRunning = 8;
    public static readonly TimeSpan RunningInterval = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How long the save must have been left alone before it is copied while the server runs. The server writes the
    /// file in place, and a copy taken mid-write would be a backup that does not load.
    /// </summary>
    public static readonly TimeSpan Settled = TimeSpan.FromSeconds(30);

    private const string StampFormat = "yyyyMMdd-HHmmss";

    public sealed record Backup(string SavePath, string SaveName, DateTime TakenAt, Kind Kind)
    {
        public string? SessionPath => File.Exists(SavePreparer.SessionPath(SavePath)) ? SavePreparer.SessionPath(SavePath) : null;
        public string Label => $"{TakenAt:yyyy-MM-dd HH:mm} " + Kind switch
        {
            Kind.Start => "server start",
            Kind.Running => "while running",
            _ => "before an older version was loaded",
        };
    }

    public static string Root(ServerPaths paths) => Path.Combine(paths.DataDir, "ModderLords", "save-backups");

    public static string DirFor(ServerPaths paths, string saveName) => Path.Combine(Root(paths), saveName);

    /// <summary>The backups of one save, newest first. Files that do not follow the naming are left out, never deleted.</summary>
    public static IReadOnlyList<Backup> List(ServerPaths paths, string saveName)
    {
        var dir = DirFor(paths, saveName);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.sav")
            .Select(f => Parse(f, saveName))
            .OfType<Backup>()
            .OrderByDescending(b => b.TakenAt)
            .ToList();
    }

    /// <summary>
    /// Copies the save (and its session file) when it changed since the newest backup. Returns the new backup, or
    /// null when there was nothing to copy: no save, an unchanged one, or (<paramref name="serverRunning"/>) one the
    /// server wrote too recently to be sure it has finished.
    /// </summary>
    /// <param name="always">Copy even when the save looks unchanged: for <see cref="Kind.Replaced"/>, where the save is
    /// about to be overwritten and its session file may have changed on its own.</param>
    public static Backup? Take(ServerPaths paths, string saveName, Kind kind, DateTime now, bool serverRunning = false, bool always = false)
    {
        SavePreparer.ValidateSaveName(saveName);
        var source = new FileInfo(SavePreparer.SavePath(paths, saveName));
        if (!source.Exists) return null;
        if (serverRunning && now.ToUniversalTime() - source.LastWriteTimeUtc < Settled) return null;

        // File.Copy keeps the modification time, so an unchanged save is recognised by length and time alone, without
        // reading tens of megabytes to compare them.
        if (!always && List(paths, saveName).FirstOrDefault() is { } newest
            && new FileInfo(newest.SavePath) is { Exists: true } last
            && last.Length == source.Length && last.LastWriteTimeUtc == source.LastWriteTimeUtc)
            return null;

        var dir = DirFor(paths, saveName);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{kind.ToString().ToLowerInvariant()}.sav");
        if (File.Exists(target)) return null;

        var before = (source.Length, source.LastWriteTimeUtc);
        CopyAtomically(source.FullName, target);
        source.Refresh();
        // The server wrote while the copy was being made: what was copied is a mix of two saves.
        if ((source.Length, source.LastWriteTimeUtc) != before)
        {
            File.Delete(target);
            return null;
        }
        var session = SavePreparer.SessionPath(source.FullName);
        if (File.Exists(session)) CopyAtomically(session, SavePreparer.SessionPath(target));

        Prune(paths, saveName, kind);
        return Parse(target, saveName);
    }

    // ---- versions: what the Saves tab offers to load ---------------------------------------------------------------

    public enum VersionKind { Current, CoopPrevious, Backup }

    /// <summary>
    /// One loadable state of a save: the save itself, one of Coop's own previous copies beside it, or one of ours.
    /// <see cref="Day"/> and the label are for display; <see cref="SavePath"/> is what <see cref="LoadVersion"/> copies.
    /// </summary>
    public sealed record SaveVersion(string SavePath, VersionKind Kind, DateTime WrittenAt, string Label, Backup? Backup = null)
    {
        public string? SessionPath => File.Exists(SavePreparer.SessionPath(SavePath)) ? SavePreparer.SessionPath(SavePath) : null;
        public bool IsCurrent => Kind == VersionKind.Current;
    }

    private static readonly System.Text.RegularExpressions.Regex CoopCopyName = new(@"^(?<save>.+)\.backup(?<n>[0-9]+)$");

    /// <summary>
    /// True for Coop's own previous copy of a save (&lt;save&gt;.backupN), which the Saves tab shows inside that save
    /// rather than as a save of its own. <paramref name="baseName"/> is the save it belongs to.
    /// </summary>
    public static bool IsCoopCopy(string name, out string baseName)
    {
        var m = CoopCopyName.Match(name);
        baseName = m.Success ? m.Groups["save"].Value : "";
        return m.Success;
    }

    /// <summary>
    /// Every version of a save the Saves tab can load: the save as it is now first, then Coop's two previous copies
    /// and the launcher's backups, newest first. Empty when the save does not exist.
    /// </summary>
    public static IReadOnlyList<SaveVersion> Versions(ServerPaths paths, string saveName)
    {
        var current = new FileInfo(SavePreparer.SavePath(paths, saveName));
        if (!current.Exists) return [];
        var older = new List<SaveVersion>();
        for (var n = 1; n <= 9; n++)
        {
            var copy = new FileInfo(Path.Combine(paths.SavesDir, $"{saveName}.backup{n}.sav"));
            if (!copy.Exists) continue;
            var at = copy.LastWriteTime;
            older.Add(new SaveVersion(copy.FullName, VersionKind.CoopPrevious, at,
                $"{at:yyyy-MM-dd HH:mm} Coop's {(n == 1 ? "previous save" : n == 2 ? "save before that" : $"copy {n}")}"));
        }
        older.AddRange(List(paths, saveName).Select(b => new SaveVersion(b.SavePath, VersionKind.Backup, b.TakenAt, b.Label, b)));
        var versions = new List<SaveVersion>
        {
            new(current.FullName, VersionKind.Current, current.LastWriteTime, $"{current.LastWriteTime:yyyy-MM-dd HH:mm} current save (newest)"),
        };
        versions.AddRange(older.OrderByDescending(v => v.WrittenAt));
        return versions;
    }

    /// <summary>
    /// Puts an older version in the save's place, for the next launch to host. The save as it is now is backed up
    /// first (<see cref="Kind.Replaced"/>, with its session file), so this can always be undone from the same list.
    ///
    /// The session file travels with the version: copied over when the version has one, and removed when it has none,
    /// because the current one describes a later world and would hand players characters it does not hold. Returns
    /// the backup of the save as it was, or null when <paramref name="version"/> is the current save (nothing to do).
    /// </summary>
    public static Backup? LoadVersion(ServerPaths paths, string saveName, SaveVersion version, DateTime now)
    {
        SavePreparer.ValidateSaveName(saveName);
        if (version.IsCurrent) return null;
        if (!File.Exists(version.SavePath)) throw new FileNotFoundException($"The chosen version of '{saveName}' is no longer there.", version.SavePath);
        var target = SavePreparer.SavePath(paths, saveName);
        if (Path.GetFullPath(version.SavePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return null;

        var kept = File.Exists(target)
            ? Take(paths, saveName, Kind.Replaced, now, always: true)
              ?? throw new IOException($"Could not back up '{saveName}' before replacing it; nothing was changed.")
            : null;

        var targetSession = SavePreparer.SessionPath(target);
        // Session first: a .sav paired with the wrong session file, even briefly, is a world with the wrong owners.
        if (version.SessionPath is { } session) CopyAtomically(session, targetSession);
        else if (File.Exists(targetSession)) File.Delete(targetSession);   // its copy is in the backup taken above
        CopyAtomically(version.SavePath, target);
        // File.Copy kept the old timestamp; the save is written now as far as anyone looking at it is concerned.
        File.SetLastWriteTimeUtc(target, now.ToUniversalTime());
        return kept;
    }

    private static void Prune(ServerPaths paths, string saveName, Kind kind)
    {
        var keep = kind == Kind.Running ? KeepRunning : KeepStart;
        foreach (var old in List(paths, saveName).Where(b => b.Kind == kind).Skip(keep))
        {
            try
            {
                File.Delete(old.SavePath);
                var session = SavePreparer.SessionPath(old.SavePath);
                if (File.Exists(session)) File.Delete(session);
            }
            catch (IOException) { /* in use or already gone; the next prune tries again */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Backup? Parse(string path, string saveName)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        if (dash <= 0) return null;
        if (!DateTime.TryParseExact(name[..dash], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
        Kind? kind = name[(dash + 1)..] switch { "start" => Kind.Start, "running" => Kind.Running, "replaced" => Kind.Replaced, _ => null };
        return kind is null ? null : new Backup(path, saveName, at, kind.Value);
    }

    private static void CopyAtomically(string from, string to)
    {
        var tmp = to + ".modderlords-tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }
}
