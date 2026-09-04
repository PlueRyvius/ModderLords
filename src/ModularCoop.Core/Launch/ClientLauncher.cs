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
    /// <summary>The TaleWorlds launcher, where the player picks their module list.</summary>
    public const string LauncherExeName = "Bannerlord.exe";

    /// <summary>The game itself, started with whatever module list the launcher last saved.</summary>
    public const string GameExeName = "Bannerlord.Native.exe";

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

    /// <summary>True when a client or the TaleWorlds launcher is already up, so we don't start a second one.</summary>
    public static bool IsClientRunning()
        => Process.GetProcessesByName("Bannerlord").Length > 0
           || Process.GetProcessesByName("Bannerlord.Native").Length > 0;

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
