using System.Diagnostics;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Profiles;

namespace ModularCoop.Core.Launch;

/// <summary>
/// Starts the Bannerlord client straight from the game install.
///
/// While a coop server is running, Steam's Play button for Bannerlord is unavailable: the Coop module
/// logs the server on as a Steam game server for app 261550 (Coop.Steam's SteamGameServerBoot, which the
/// server package hash-pins, so it cannot be switched off), and Steam then treats the app as already
/// running. Launching the exe directly is unaffected — Steam still sees the client and signs it in
/// normally — so a host on a single machine can join their own server.
/// </summary>
public static class ClientLauncher
{
    /// <summary>
    /// The TaleWorlds launcher UI (its FileDescription is "BannerlordLauncher"), which is what Steam's Play button
    /// opens. Bannerlord.exe is NOT this: it is the starter, and it is the wrong thing to run — see below.
    /// </summary>
    public const string LauncherExeName = "TaleWorlds.MountAndBlade.Launcher.exe";

    /// <summary>
    /// The game starter ("BannerlordStarter"). Started bare it loads the default module set: the game takes its
    /// list from the _MODULES_*...*_MODULES_ argument the launcher builds, NOT from LauncherData.xml, so running
    /// this instead of the launcher silently drops every mod, Coop included. Only a fallback.
    /// </summary>
    public const string GameExeName = "Bannerlord.exe";

    public static string ClientBin(string gameRoot) => Path.Combine(gameRoot, "bin", "Win64_Shipping_Client");

    /// <summary>Game install for this profile: the pinned one, else the first Steam library that has it.</summary>
    public static string? ResolveGameRoot(Profile profile)
        => profile.GameRoot ?? ModuleCatalog.FindGameRoot(ServerPaths.SteamLibraries());

    /// <summary>
    /// The exe to run. Defaults to the TaleWorlds launcher so the player still chooses modules;
    /// <paramref name="skipLauncher"/> starts the game directly instead. Null when neither exists.
    /// </summary>
    public static string? FindExe(string? gameRoot, bool skipLauncher = false)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return null;
        var bin = ClientBin(gameRoot);
        foreach (var name in skipLauncher ? new[] { GameExeName, LauncherExeName } : new[] { LauncherExeName, GameExeName })
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The launcher, the starter and the game itself, by process name.</summary>
    private static readonly string[] ClientProcessNames =
        ["TaleWorlds.MountAndBlade.Launcher", "Launcher.Native", "Bannerlord", "Bannerlord.Native"];

    /// <summary>True when a client or the TaleWorlds launcher is already up, so we don't start a second one.</summary>
    public static bool IsClientRunning() => ClientProcessNames.Any(n => Process.GetProcessesByName(n).Length > 0);

    /// <summary>
    /// Starts the client detached: no job object (unlike the engine, it must survive closing the launcher),
    /// no redirected streams, working directory set to the exe's own folder as Steam does.
    /// </summary>
    public static Process Start(string exe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start " + exe);
    }
}
