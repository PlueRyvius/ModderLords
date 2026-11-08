namespace ModderLords.Core.Launch;

/// <summary>
/// Keeps the TaleWorlds logo video from playing in a game ModderLords starts, and only there.
///
/// Module.SetInitialModuleScreenAsRootScreen plays Native\Videos\TWLogo_and_Partners.ivf once, at the first main menu,
/// if that file exists (VERIFIED 2026-11-08, v1.4.8.119303); there is no launch option or config value for it, and the
/// player's game runs on .NET Framework, so no startup hook reaches it either. So a launch moves the file aside
/// (".modderlords-hidden" next to it) and it is moved back when that game exits, or when ModderLords next starts if
/// it was closed first. Every hidden path is written to a ledger before the move, so a crash in between still restores.
/// A launch from the TaleWorlds launcher while the file is hidden skips the video too; nothing else reads it.
/// </summary>
public static class IntroVideo
{
    public const string Suffix = ".modderlords-hidden";

    public static string DefaultLedger => Path.Combine(Profiles.ProfileStore.RootDir, "hidden-intro-video.txt");

    public static string VideoPath(string gameRoot) =>
        Path.Combine(GamePaths.ModulesDir(gameRoot), "Native", "Videos", "TWLogo_and_Partners.ivf");

    /// <summary>Moves the video aside for one launch. Null when it is out of the way; otherwise why it is not.</summary>
    public static string? Hide(string gameRoot, string? ledger = null)
    {
        ledger ??= DefaultLedger;
        var video = VideoPath(gameRoot);
        var hidden = video + Suffix;
        try
        {
            if (!File.Exists(video)) return null;
            // Both present: Steam put the file back (an update or a verify) while it was hidden.
            if (File.Exists(hidden)) File.Delete(hidden);
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            if (!File.Exists(ledger) || !File.ReadAllLines(ledger).Contains(video, StringComparer.OrdinalIgnoreCase))
                File.AppendAllLines(ledger, [video]);
            File.Move(video, hidden);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not skip the intro video (" + ex.Message + "); it plays";
        }
    }

    /// <summary>Puts back every video a launch hid. Callers check first that no game ModderLords started is running.</summary>
    public static void RestoreAll(string? ledger = null)
    {
        ledger ??= DefaultLedger;
        try
        {
            if (!File.Exists(ledger)) return;
            var left = File.ReadAllLines(ledger).Where(l => l.Length > 0 && !Restore(l)).ToList();
            if (left.Count == 0) File.Delete(ledger);
            else File.WriteAllLines(ledger, left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True once <paramref name="video"/> is back (or there is nothing to put back).</summary>
    private static bool Restore(string video)
    {
        var hidden = video + Suffix;
        try
        {
            if (!File.Exists(hidden)) return true;
            if (File.Exists(video)) File.Delete(hidden);
            else File.Move(hidden, video);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Restores once <paramref name="game"/> exits and no other copy of the game is still running.</summary>
    public static void RestoreWhenExited(System.Diagnostics.Process game)
    {
        try
        {
            game.EnableRaisingEvents = true;
            game.Exited += (_, _) => { if (!ClientLauncher.IsClientRunning()) RestoreAll(); };
            if (game.HasExited && !ClientLauncher.IsClientRunning()) RestoreAll();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
