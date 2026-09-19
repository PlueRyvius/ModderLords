namespace ModderLords.Core.Modules;

/// <summary>
/// Mark-of-the-web on a module's assemblies, and how to take it off.
///
/// Windows tags every file extracted from a downloaded zip with an alternate data stream named Zone.Identifier.
/// The .NET loader refuses to load a tagged assembly from a zone it does not trust, so Bannerlord starts with the
/// mod enabled in the launcher and simply none of its code running — no error, no missing folder, nothing in the
/// log that names the cause. From here that mod then reads as "installed but doing nothing", and against a server
/// it reads as a client that does not really have the mod. Nearly every report of that shape is a blocked zip that
/// the player never right-clicked → Unblock before extracting.
///
/// Clearing the tag is exactly what that Unblock checkbox does: delete the stream. It needs no elevation beyond
/// write access to the file itself, and there is nothing to undo — the stream carries no information the game or
/// the launcher uses.
/// </summary>
public static class BlockedFiles
{
    private const string Stream = ":Zone.Identifier";

    /// <summary>The extensions worth looking at: only assemblies are refused a load, so only assemblies matter.</summary>
    private static readonly string[] Assemblies = { ".dll", ".exe" };

    public static bool IsBlocked(string file)
    {
        try { return File.Exists(file + Stream); }
        catch { return false; }   // not NTFS, or the path is unreadable; either way there is nothing to clear.
    }

    /// <summary>Drops the mark from one file. Best effort: a locked or read-only file is not worth failing over.</summary>
    public static bool Unblock(string file)
    {
        try { File.Delete(file + Stream); return true; }
        catch { return false; }
    }

    /// <summary>Every blocked assembly under a folder, or an empty list if it cannot be walked.</summary>
    public static IReadOnlyList<string> Find(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Assemblies.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(IsBlocked)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Unblocks the given files; returns how many were actually cleared.</summary>
    public static int UnblockAll(IEnumerable<string> files) => files.Count(f => Unblock(f));

    /// <summary>Unblocks everything under a folder. Used after copying a folder out of a release zip.</summary>
    public static int UnblockFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        try { return UnblockAll(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)); }
        catch { return 0; }
    }
}
