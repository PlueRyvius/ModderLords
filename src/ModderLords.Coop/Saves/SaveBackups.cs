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
/// Two kinds, pruned separately so a long session cannot push out the copy taken before it started:
/// <list type="bullet">
/// <item><see cref="Kind.Start"/>: the save as it was when the server was launched.</item>
/// <item><see cref="Kind.Running"/>: taken while the server runs, at most once per <see cref="RunningInterval"/>, and
/// once more after it exits.</item>
/// </list>
/// A copy is only taken when the save changed since the newest backup, so a server that crashes on every boot does
/// not rotate the good copies out with copies of the broken one.
///
/// Backups live under the server's data folder (ModderLords\save-backups\&lt;save&gt;), not in Game Saves, so the
/// Saves tab and the server never mistake one for a world to host. Restoring copies one into Game Saves under a new
/// name; nothing here ever writes to a save the server uses.
/// </summary>
public static class SaveBackups
{
    public enum Kind { Start, Running }

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
        public string Label => $"{TakenAt:yyyy-MM-dd HH:mm} {(Kind == Kind.Start ? "server start" : "while running")}";
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
    public static Backup? Take(ServerPaths paths, string saveName, Kind kind, DateTime now, bool serverRunning = false)
    {
        SavePreparer.ValidateSaveName(saveName);
        var source = new FileInfo(SavePreparer.SavePath(paths, saveName));
        if (!source.Exists) return null;
        if (serverRunning && now.ToUniversalTime() - source.LastWriteTimeUtc < Settled) return null;

        // File.Copy keeps the modification time, so an unchanged save is recognised by length and time alone, without
        // reading tens of megabytes to compare them.
        if (List(paths, saveName).FirstOrDefault() is { } newest
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

    /// <summary>
    /// Copies a backup into Game Saves as a new save, with its session file, and returns the new save's name. The
    /// save it came from is not touched.
    /// </summary>
    public static string Restore(ServerPaths paths, Backup backup, DateTime now)
    {
        var name = $"{backup.SaveName}_restored_{backup.TakenAt.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        for (var i = 2; SavePreparer.Exists(paths, name); i++)
            name = $"{backup.SaveName}_restored_{backup.TakenAt.ToString(StampFormat, CultureInfo.InvariantCulture)}_{i}";
        SavePreparer.ValidateSaveName(name);

        Directory.CreateDirectory(paths.SavesDir);
        var target = SavePreparer.SavePath(paths, name);
        // Session first: a .sav that appears without its session file, even briefly, is a world with unowned players.
        if (backup.SessionPath is { } session) CopyAtomically(session, SavePreparer.SessionPath(target));
        CopyAtomically(backup.SavePath, target);
        return name;
    }

    private static void Prune(ServerPaths paths, string saveName, Kind kind)
    {
        var keep = kind == Kind.Start ? KeepStart : KeepRunning;
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
        Kind? kind = name[(dash + 1)..] switch { "start" => Kind.Start, "running" => Kind.Running, _ => null };
        return kind is null ? null : new Backup(path, saveName, at, kind.Value);
    }

    private static void CopyAtomically(string from, string to)
    {
        var tmp = to + ".modderlords-tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }
}
