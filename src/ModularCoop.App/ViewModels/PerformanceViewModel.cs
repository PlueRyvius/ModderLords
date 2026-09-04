using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ModularCoop.Core.Logs;
using ModularCoop.Core.Perf;
using ModularCoop.Core.Profiles;

namespace ModularCoop.App.ViewModels;

/// <summary>
/// One metric on the Performance tab: the current value, this session's normal range, and a sentence about it.
/// </summary>
public partial class MetricRow : ObservableObject
{
    private readonly BaselineTracker _tracker;
    private readonly Func<double, string> _format;

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

    public MetricRow(string title, string explanation, BaselineTracker tracker, Func<double, string> format)
    {
        Title = title;
        Explanation = explanation;
        _tracker = tracker;
        _format = format;
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
        Departed = _tracker.IsDeparted;
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

    public IReadOnlyList<MetricRow> Metrics { get; }

    [ObservableProperty] private string _campaignMode = "—";
    [ObservableProperty] private string _players = "—";
    [ObservableProperty] private string _playersNote = "Enable Settings sync to see the player count.";
    [ObservableProperty] private string _sessionState = "Not running.";

    private DateTimeOffset? _sessionStart;
    private readonly List<string> _departures = [];

    public PerformanceViewModel() => Metrics = [TickRate, WorstFrame, Cpu, Memory];

    public void SessionStarted()
    {
        _sessionStart = DateTimeOffset.Now;
        _departures.Clear();
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
        }
        UpdateSessionState();
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
