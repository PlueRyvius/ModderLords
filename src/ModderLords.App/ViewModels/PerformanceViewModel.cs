using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ModderLords.Core.Logs;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;

namespace ModderLords.App.ViewModels;

/// <summary>
/// One metric on the Performance tab: the current value, this session's normal range, and a sentence about it.
/// </summary>
public partial class MetricRow : ObservableObject
{
    private readonly BaselineTracker _tracker;
    private readonly Func<double, string> _format;
    private readonly bool _watched;

    public string Title { get; }
    public string Explanation { get; }

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _status = "No data yet.";
    [ObservableProperty] private bool _departed;
    /// <summary>History for the sparkline, oldest first.</summary>
    [ObservableProperty] private IReadOnlyList<double> _history = [];
    [ObservableProperty] private double _bandLow;
    [ObservableProperty] private double _bandHigh;
    [ObservableProperty] private bool _hasBand;

    /// <param name="watched">False for a metric that is shown but never raises the "outside its normal range" warning.</param>
    public MetricRow(string title, string explanation, BaselineTracker tracker, Func<double, string> format, bool watched = true)
    {
        Title = title;
        Explanation = explanation;
        _tracker = tracker;
        _format = format;
        _watched = watched;
    }

    /// <summary>Shows a reading that says nothing about normal running (nobody connected), without recording it.</summary>
    public void Idle(double v, string status)
    {
        Value = _format(v);
        Status = status;
    }

    public void Add(double v)
    {
        _tracker.Add(v);
        Refresh();
    }

    public void Refresh()
    {
        Value = _tracker.Latest is { } v ? _format(v) : "—";
        Status = _tracker.Status(_format);
        Departed = _watched && _tracker.IsDeparted;
        History = _tracker.Samples();
        if (_tracker.Band() is { } band)
        {
            (BandLow, BandHigh, HasBand) = (band.Low, band.High, true);
        }
        else HasBand = false;
    }

    public BaselineTracker.Summary Summary() => _tracker.Describe();
}

/// <summary>
/// The Performance tab. Fed entirely from samples that already arrive on the console flush tick, so nothing here
/// adds work per engine line, and the server-side cost is one printed line every ten seconds.
/// </summary>
public partial class PerformanceViewModel : ObservableObject
{
    public MetricRow TickRate { get; } = new("Tick rate", "Engine frames per second, averaged over each 10-second window. The same thing Coop's own FPS log counts — not campaign hours per second.",
        new BaselineTracker("tickRate", lowIsBad: true), v => $"{v:0.0}/s");

    public MetricRow WorstFrame { get; } = new("Worst frame", "The slowest single frame in each window, as a rate. A low number here is a visible stall even when the average looks fine.",
        new BaselineTracker("worstFrame", lowIsBad: true), v => $"{v:0.0}/s");

    public MetricRow Cpu { get; } = new("Engine CPU", "Share of this machine the server process used, across all cores. Measured by the launcher, so it costs the server nothing.",
        new BaselineTracker("cpuPercent", lowIsBad: false), v => $"{v:0.0}%");

    public MetricRow Memory { get; } = new("Engine memory", "Working set of the server process. The number that gave the game away when the launcher ran away with 20 GB.",
        new BaselineTracker("workingSetMb", lowIsBad: false), v => v >= 1024 ? $"{v / 1024:0.00} GB" : $"{v:0} MB");

    // Not watched: traffic follows how many players are on and what they are doing, so a rise is not a fault.
    public MetricRow Upload { get; } = new("Upload", "Data the server sends to players, all of them together. This is the figure to hold against your connection's upload speed. Read from the network library's own running totals, so measuring it costs the server nothing.",
        new BaselineTracker("uploadKbps", lowIsBad: false), FormatRate, watched: false);

    public MetricRow Download { get; } = new("Download", "Data the server receives from players, all of them together.",
        new BaselineTracker("downloadKbps", lowIsBad: false), FormatRate, watched: false);

    public IReadOnlyList<MetricRow> Metrics { get; }

    /// <summary>A rate in kilobits per second, as people read connection speeds: 850 kbit/s, 1.25 Mbit/s.</summary>
    internal static string FormatRate(double kbps) => kbps >= 1000 ? $"{kbps / 1000:0.00} Mbit/s" : $"{kbps:0} kbit/s";

    internal static string FormatData(double megabytes) => megabytes >= 1000 ? $"{megabytes / 1000:0.00} GB" : $"{megabytes:0.0} MB";

    [ObservableProperty] private string _campaignMode = "—";
    [ObservableProperty] private string _players = "—";
    [ObservableProperty] private string _playersNote = "Enable Settings sync to see the player count.";
    [ObservableProperty] private string _sessionState = "Not running.";
    [ObservableProperty] private string _dataTotals = "—";

    private DateTimeOffset? _sessionStart;
    private readonly List<string> _departures = [];

    public PerformanceViewModel() => Metrics = [TickRate, WorstFrame, Cpu, Memory, Upload, Download];

    public void SessionStarted()
    {
        _sessionStart = DateTimeOffset.Now;
        _departures.Clear();
        DataTotals = "—";
        SessionState = "Warming up — the first samples after a launch are world load, not normal running.";
    }

    /// <summary>A perf line from a module, already parsed off the read thread.</summary>
    public void Apply(PerfSample sample)
    {
        switch (sample.Kind)
        {
            case PerfKind.Tick:
                if (sample.Number("avgFps") is { } avg) Track(TickRate, avg);
                if (sample.Number("minFps") is { } min) Track(WorstFrame, min);
                if (sample.Text("campaignMode") is { } mode)
                    CampaignMode = mode switch
                    {
                        "none" => "no campaign loaded",
                        "unknown" => "unknown",
                        "Stop" => "paused",
                        _ => mode,
                    };
                break;
            case PerfKind.Players:
                if (sample.Count("count") is { } n)
                {
                    Players = n.ToString();
                    PlayersNote = n == 0 ? "Nobody connected — a quiet server ticks differently to a busy one." : "";
                }
                break;
            case PerfKind.Net:
                ApplyNet(sample);
                break;
        }
        UpdateSessionState();
    }

    private void ApplyNet(PerfSample sample)
    {
        var peers = sample.Count("peers");
        if (peers is { } n)
        {
            Players = n.ToString();
            PlayersNote = n == 0 ? "Nobody connected — a quiet server ticks differently to a busy one." : "";
        }
        // With nobody connected the rates are zero. Recording that would make zero the normal range.
        foreach (var (row, key) in new[] { (Upload, "upKbps"), (Download, "downKbps") })
        {
            if (sample.Number(key) is not { } rate) continue;
            if (peers == 0) row.Idle(rate, "Nobody connected.");
            else Track(row, rate);
        }
        if (sample.Number("sentMb") is { } sent && sample.Number("receivedMb") is { } received)
            DataTotals = $"{FormatData(sent)} sent, {FormatData(received)} received";
    }

    public void Apply(ResourceSample sample)
    {
        Track(Cpu, sample.CpuPercent);
        Track(Memory, sample.WorkingSetBytes / 1024.0 / 1024.0);
        UpdateSessionState();
    }

    private void Track(MetricRow row, double value)
    {
        var wasDeparted = row.Departed;
        row.Add(value);
        if (row.Departed && !wasDeparted)
            _departures.Add($"{DateTimeOffset.Now:HH:mm:ss} {row.Title}: {row.Value} ({row.Status})");
    }

    private void UpdateSessionState()
    {
        var departed = Metrics.Where(m => m.Departed).Select(m => m.Title).ToList();
        SessionState = departed.Count > 0
            ? "Watching: " + string.Join(", ", departed) + " outside the normal range for this session."
            : TickRate.History.Count == 0 ? "Warming up — no samples past the warm-up window yet."
            : "Everything within this session's normal range.";
    }

    /// <summary>
    /// Writes what this session saw, so thresholds can later be set from real data instead of guesses. Small, and
    /// only on stop or every few minutes — never per sample.
    /// </summary>
    public string? WriteSessionSummary(string profileName)
    {
        if (_sessionStart is null || TickRate.History.Count == 0) return null;
        try
        {
            var dir = Path.Combine(ProfileStore.RootDir, "perf");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{ProfileStore.Safe(profileName)}-{_sessionStart:yyyyMMdd-HHmmss}.json");
            var payload = new
            {
                profile = profileName,
                sessionStart = _sessionStart,
                sessionEnd = DateTimeOffset.Now,
                metrics = Metrics.Select(m => m.Summary()),
                departures = _departures,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch
        {
            return null;   // a diagnostic that cannot be written is not worth an error dialog
        }
    }
}
