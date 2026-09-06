using System;
using System.Globalization;
using TaleWorlds.CampaignSystem;

namespace ModderLords.Compat;

/// <summary>
/// Measures the server's frame pace and reports it once every ten seconds.
///
/// The cost is the point. v0.8.3 nearly took a machine down because the engine's trace switches printed hundreds of
/// thousands of lines a second; the expensive part of that was never the measuring, it was the logging. So this
/// keeps four numbers in fields, does one add and one compare per frame, allocates nothing, and prints a single
/// line per window. The launcher parses that line; nobody has to read it.
///
/// What it measures is the engine's application tick — the same thing Coop's own FpsLogger counts — not campaign
/// hours per second. Comparable to Coop's numbers, but worth being honest about in the UI.
/// </summary>
internal sealed class PerfSampler
{
    /// <summary>Long enough that a window is meaningful and the log stays quiet; short enough to notice a problem while it is happening.</summary>
    public const float ReportPeriodSeconds = 10f;

    private float _sinceReport;
    private int _frames;
    private double _sumSeconds;
    private float _worstFrame;

    public void Tick(float dt)
    {
        if (dt <= 0f) return;   // a paused or stalled frame tells us nothing about pace
        _frames++;
        _sumSeconds += dt;
        if (dt > _worstFrame) _worstFrame = dt;
        _sinceReport += dt;
        if (_sinceReport < ReportPeriodSeconds) return;

        Report();
        _sinceReport = 0f;
        _frames = 0;
        _sumSeconds = 0;
        _worstFrame = 0f;
    }

    private void Report()
    {
        if (_frames == 0 || _sumSeconds <= 0) return;
        var avgFps = _frames / _sumSeconds;
        var minFps = _worstFrame > 0f ? 1.0 / _worstFrame : 0.0;

        // One line, key=value, invariant culture so a comma decimal separator cannot break the parser.
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "[ModderLords] perf tick avgFps={0:0.0} minFps={1:0.0} frames={2} windowSec={3:0.0} campaignMode={4}",
            avgFps, minFps, _frames, _sumSeconds, CampaignMode()));
    }

    /// <summary>
    /// Whether campaign time is actually running, so a low frame rate on a paused server does not read as a problem.
    /// A property read, guarded: outside a campaign (loading, menus) there is nothing to report.
    /// </summary>
    private static string CampaignMode()
    {
        try
        {
            var campaign = Campaign.Current;
            return campaign is null ? "none" : campaign.TimeControlMode.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}
