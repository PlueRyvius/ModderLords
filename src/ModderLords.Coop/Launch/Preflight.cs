using System.Diagnostics;
using System.Net.NetworkInformation;

using ModderLords.Core.Compat;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>Checks that stop a launch before it can fail confusingly: ports in use, an engine already running, hook missing.</summary>
public static class Preflight
{
    public sealed record Problem(string Message, bool Blocking);

    public static IReadOnlyList<Problem> Run(ServerPaths paths, int joinPort, int enginePort, bool modsSelected)
    {
        var list = new List<Problem>();

        foreach (var p in paths.Validate()) list.Add(new Problem(p, true));

        var udp = new HashSet<int>();
        try { foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()) udp.Add(ep.Port); } catch { }
        if (udp.Contains(joinPort)) list.Add(new Problem($"UDP port {joinPort} (join port) is already in use. Another server running?", true));
        if (udp.Contains(enginePort)) list.Add(new Problem($"UDP port {enginePort} (engine port) is already in use.", true));

        foreach (var other in RunningEngines(paths))
            list.Add(new Problem($"An engine from this server package is already running (pid {other}). Stop it first.", true));

        if (modsSelected && HookSetup.LocateHook() is null)
            list.Add(new Problem("ModderLords.Hook.dll is missing next to the launcher; mods with helper DLLs will not load. Reinstall the release zip.", true));

        // The host usually plays on this PC too, so its client settings are worth checking before a session starts.
        foreach (var w in ClientGraphicsCheck.Warnings(ClientGraphicsCheck.DefaultConfigPath())) list.Add(new Problem(w, false));

        var free = FreeDiskGb(paths.DataDir);
        if (free is < 2) list.Add(new Problem($"Only {free:0.#} GB free on the drive holding the saves.", false));

        return list;
    }

    /// <summary>Process ids of dotnet.exe instances whose executable lives under this server package.</summary>
    public static IEnumerable<int> RunningEngines(ServerPaths paths)
    {
        var dotnet = Path.GetFullPath(paths.DotnetExe);
        foreach (var p in Process.GetProcessesByName("dotnet"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path is not null && string.Equals(Path.GetFullPath(path), dotnet, StringComparison.OrdinalIgnoreCase)) yield return p.Id;
            p.Dispose();
        }
    }

    private static double? FreeDiskGb(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is null) return null;
            return new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024 / 1024;
        }
        catch { return null; }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> files matching the pattern, and then keeps deleting
    /// oldest-first until what remains fits in <paramref name="maxTotalBytes"/>.
    ///
    /// A count alone is not a limit: keeping 20 launch logs let this folder reach 40 GB, because a
    /// single session with an engine trace switch on wrote 9.5 GB. Size is the limit that matters,
    /// so the newest file is always kept even if it alone exceeds the budget — deleting the log of
    /// the session someone is trying to diagnose would defeat the point.
    /// </summary>
    public static int RotateLogs(string dir, string pattern, int keep, long maxTotalBytes = 2L * 1024 * 1024 * 1024)
    {
        if (!Directory.Exists(dir)) return 0;
        var all = new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var n = 0;

        var doomed = all.Skip(keep).ToList();
        foreach (var f in doomed) { try { f.Delete(); n++; } catch { } }

        var survivors = all.Take(keep).ToList();
        var total = survivors.Sum(f => SafeLength(f));
        // Oldest first, never the newest.
        for (var i = survivors.Count - 1; i >= 1 && total > maxTotalBytes; i--)
        {
            var len = SafeLength(survivors[i]);
            try { survivors[i].Delete(); n++; total -= len; } catch { }
        }
        return n;
    }

    /// <summary>
    /// The same policy as <see cref="RotateLogs"/>, for the engine's crash reports — which are directories, not
    /// files, and enormous: one report is a ~540 MB minidump plus the engine logs.
    ///
    /// This exists because warnings used to write one report each. That is fixed at the source now
    /// (HeadlessDebugManager stops warnings dumping), but the suppression is deliberately narrow so a real crash
    /// still leaves evidence — which means this folder can still grow, just far more slowly. Measured 2026-09-11
    /// before the fix: 227 reports, 100 GB, from a single 15-minute run.
    ///
    /// Shared with the retail game, so the newest report is never deleted: it is the one someone is trying to read.
    /// </summary>
    public static int RotateCrashDirs(string dir, int keep = 5, long maxTotalBytes = 5L * 1024 * 1024 * 1024)
    {
        if (!Directory.Exists(dir)) return 0;
        var all = new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).ToList();
        var n = 0;

        foreach (var d in all.Skip(keep)) { try { d.Delete(recursive: true); n++; } catch { } }

        var survivors = all.Take(keep).ToList();
        var total = survivors.Sum(SafeSize);
        for (var i = survivors.Count - 1; i >= 1 && total > maxTotalBytes; i--)
        {
            var len = SafeSize(survivors[i]);
            try { survivors[i].Delete(recursive: true); n++; total -= len; } catch { }
        }
        return n;
    }

    private static long SafeSize(DirectoryInfo d)
    {
        try { return d.GetFiles("*", SearchOption.AllDirectories).Sum(SafeLength); } catch { return 0; }
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; }
    }
}
