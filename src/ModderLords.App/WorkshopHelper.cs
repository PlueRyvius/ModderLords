using System.Diagnostics;
using System.IO;
using System.Text;
using ModderLords.Core.Workshop;

namespace ModderLords.App;

/// <summary>
/// Both ends of a Workshop subscribe. The app starts itself with <see cref="SteamWorkshop.SubscribeArg"/>; that child
/// talks to Steam and writes one <see cref="WorkshopEvent"/> per line, and the parent reads them back as they arrive.
/// </summary>
public static class WorkshopHelper
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Child side: <c>--workshop-subscribe --game-root &lt;dir&gt; id id ...</c>. Returns the exit code.</summary>
    public static int Run(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Report(WorkshopEvent e) => stdout.WriteLine(e.ToLine());

        string? gameRoot = null;
        var ids = new List<ulong>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == SteamWorkshop.GameRootArg && i + 1 < args.Length) gameRoot = args[++i];
            else if (ulong.TryParse(args[i], out var id)) ids.Add(id);
        }
        if (gameRoot is null)
        {
            Report(new WorkshopEvent(0, WorkshopEventKind.SteamUnavailable, "Bannerlord install not found"));
            return 2;
        }
        try
        {
            using var steam = SteamWorkshop.Open(gameRoot);
            steam.SubscribeAndWait(ids, Report, StallTimeout);
            return 0;
        }
        catch (SteamUnavailableException ex)
        {
            Report(new WorkshopEvent(0, WorkshopEventKind.SteamUnavailable, ex.Message));
            return 3;
        }
        catch (Exception ex)
        {
            Report(new WorkshopEvent(0, WorkshopEventKind.SteamUnavailable, "Steam call failed: " + ex.Message));
            return 4;
        }
    }

    /// <summary>Parent side: starts the child and hands every event to <paramref name="onEvent"/> (on a background thread).</summary>
    public static Process Start(string gameRoot, IReadOnlyList<ulong> ids, Action<WorkshopEvent> onEvent)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find this program's own path");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(SteamWorkshop.SubscribeArg);
        psi.ArgumentList.Add(SteamWorkshop.GameRootArg);
        psi.ArgumentList.Add(gameRoot);
        foreach (var id in ids) psi.ArgumentList.Add(id.ToString());

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (WorkshopEvent.TryParse(e.Data) is { } ev) onEvent(ev); };
        p.Start();
        p.BeginOutputReadLine();
        return p;
    }

    /// <summary>
    /// The fallback: opens each item in the Steam client, where subscribing is one click. Falls back to the web page
    /// if the steam:// handler is not registered. Paced, so Steam is not handed a burst of pages at once.
    /// </summary>
    public static async Task OpenOnWorkshopAsync(IEnumerable<ulong> ids)
    {
        foreach (var id in ids)
        {
            try { Process.Start(new ProcessStartInfo(WorkshopEvent.SteamClientUrl(id)) { UseShellExecute = true }); }
            catch (Exception)
            {
                try { Process.Start(new ProcessStartInfo(WorkshopEvent.WebUrl(id)) { UseShellExecute = true }); }
                catch (Exception) { }
            }
            await Task.Delay(700);
        }
    }
}
