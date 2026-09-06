using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ModderLords.Core.Launch;

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

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; }
    }
}
