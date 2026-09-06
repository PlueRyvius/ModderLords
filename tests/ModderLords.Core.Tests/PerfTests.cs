using ModderLords.Core.Logs;
using ModderLords.Core.Perf;
using Xunit;

namespace ModderLords.Core.Tests;

public class PerfLineParserTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private const string Line = "[ModderLords] perf tick avgFps=142.7 minFps=38.2 frames=1427 windowSec=10.0 campaignMode=Real";

    [Fact]
    public void Reads_every_field_of_a_tick_line()
    {
        var s = PerfLineParser.TryParse(Line, At);
        Assert.NotNull(s);
        Assert.Equal(PerfKind.Tick, s!.Kind);
        Assert.Equal(142.7, s.Number("avgFps"));
        Assert.Equal(38.2, s.Number("minFps"));
        Assert.Equal(1427, s.Count("frames"));
        Assert.Equal("Real", s.Text("campaignMode"));
    }

    [Fact]
    public void Reads_a_players_line()
    {
        var s = PerfLineParser.TryParse("[ModderLords] perf players count=3", At);
        Assert.Equal(PerfKind.Players, s!.Kind);
        Assert.Equal(3, s.Count("count"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[ModderLords] guards installed: 9")]          // our own Tool line, not perf
    [InlineData("[ModderLords.Compat] something")]
    [InlineData("[Fps] frames=100 avg=50")]                     // Coop's own line: not ours to parse
    [InlineData("[ModderLords] perf ")]
    public void Refuses_anything_that_is_not_a_perf_line(string line)
        => Assert.Null(PerfLineParser.TryParse(line, At));

    [Fact]
    public void A_truncated_or_malformed_line_loses_fields_rather_than_throwing()
    {
        // A capped log or a half-flushed console can cut a line anywhere; ingestion must survive it.
        var s = PerfLineParser.TryParse("[ModderLords] perf tick avgFps=142.7 minFps= frames junk =x windowSec=abc", At);
        Assert.NotNull(s);
        Assert.Equal(142.7, s!.Number("avgFps"));
        Assert.Null(s.Number("minFps"));      // "minFps=" has no value
        Assert.Null(s.Number("frames"));      // no '=' at all
        Assert.Null(s.Number("windowSec"));   // present but not a number
    }

    [Fact]
    public void An_unknown_kind_is_kept_so_a_newer_module_can_talk_to_an_older_launcher()
    {
        var s = PerfLineParser.TryParse("[ModderLords] perf memory bytes=42", At);
        Assert.Equal(PerfKind.Unknown, s!.Kind);
        Assert.Equal(42, s.Count("bytes"));
    }

    /// <summary>
    /// The classifier is order-dependent: the generic "[ModderLords]" branch would swallow perf lines into Tool if
    /// the specific test were not placed in front of it.
    /// </summary>
    [Fact]
    public void Perf_lines_classify_as_Perf_not_Tool()
    {
        Assert.Equal(LogCategory.Perf, LogClassifier.Classify(Line).Category);
        Assert.Equal(LogCategory.Tool, LogClassifier.Classify("[ModderLords] mod list already matches the server").Category);
    }
}

public class ResourceMathTests
{
    [Fact]
    public void One_core_fully_used_on_a_four_core_box_is_twenty_five_percent()
        => Assert.Equal(25.0, ResourceMath.CpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 4), 3);

    [Fact]
    public void All_cores_fully_used_is_one_hundred()
        => Assert.Equal(100.0, ResourceMath.CpuPercent(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(10), 4), 3);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_backwards_interval_yields_zero_not_infinity(int wallSeconds)
        => Assert.Equal(0, ResourceMath.CpuPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(wallSeconds), 4));

    [Fact]
    public void No_processors_reported_yields_zero_rather_than_dividing_by_zero()
        => Assert.Equal(0, ResourceMath.CpuPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 0));
}

public class BaselineTrackerTests
{
    private static BaselineTracker Warm(bool lowIsBad = true, double value = 100)
    {
        var t = new BaselineTracker("test", lowIsBad);
        for (var i = 0; i < BaselineTracker.WarmUpSamples; i++) t.Add(value);
        return t;
    }

    [Fact]
    public void Warm_up_samples_are_not_treated_as_normal()
    {
        var t = new BaselineTracker("test", lowIsBad: true);
        // World load: terrible numbers that must not define "normal".
        for (var i = 0; i < BaselineTracker.WarmUpSamples; i++) t.Add(3);
        Assert.Equal(0, t.SampleCount);
        Assert.False(t.WarmedUp);

        t.Add(120);
        Assert.True(t.WarmedUp);
        Assert.Equal(1, t.SampleCount);
        Assert.Equal(120, t.Median());
    }

    [Fact]
    public void Median_ignores_a_single_wild_spike()
    {
        var t = Warm();
        foreach (var v in new double[] { 100, 101, 99, 100, 5000, 100, 99 }) t.Add(v);
        Assert.Equal(100, t.Median());   // a mean would be ~800
    }

    [Fact]
    public void One_bad_sample_is_not_a_departure_but_three_in_a_row_are()
    {
        var t = Warm();
        for (var i = 0; i < 20; i++) t.Add(100 + (i % 3));

        t.Add(5);
        Assert.False(t.IsDeparted);
        t.Add(5);
        Assert.False(t.IsDeparted);   // still only two
        t.Add(5);
        Assert.True(t.IsDeparted);    // sustained
    }

    [Fact]
    public void An_in_band_sample_resets_the_run()
    {
        var t = Warm();
        for (var i = 0; i < 20; i++) t.Add(100 + (i % 3));
        t.Add(5); t.Add(5);
        t.Add(100);                   // recovered
        t.Add(5); t.Add(5);
        Assert.False(t.IsDeparted);   // the run restarted, so two is still two
    }

    [Fact]
    public void Departures_only_count_in_the_direction_that_is_bad()
    {
        var low = Warm(lowIsBad: true);
        var high = Warm(lowIsBad: false);
        for (var i = 0; i < 20; i++) { low.Add(100 + (i % 3)); high.Add(100 + (i % 3)); }
        for (var i = 0; i < 4; i++) { low.Add(5000); high.Add(5000); }

        Assert.False(low.IsDeparted);   // a tick rate far ABOVE normal is not a problem
        Assert.True(high.IsDeparted);   // CPU far above normal is
    }

    [Fact]
    public void Opposite_direction_outliers_do_not_accumulate_together()
    {
        var t = Warm(lowIsBad: false);
        for (var i = 0; i < 20; i++) t.Add(100 + (i % 3));
        t.Add(5000); t.Add(1); t.Add(5000); t.Add(1);
        Assert.False(t.IsDeparted);
    }

    [Fact]
    public void History_is_bounded_and_keeps_the_newest()
    {
        var t = Warm();
        for (var i = 0; i < BaselineTracker.Capacity + 50; i++) t.Add(i);
        var samples = t.Samples();
        Assert.Equal(BaselineTracker.Capacity, samples.Count);
        Assert.Equal(BaselineTracker.Capacity + 49, samples[^1]);
        Assert.Equal(50, samples[0]);
    }

    [Fact]
    public void A_perfectly_steady_metric_does_not_flag_every_wobble()
    {
        // MAD would be 0 here; without a floor on the spread, 100.1 would read as a departure.
        var t = Warm();
        for (var i = 0; i < 30; i++) t.Add(100);
        t.Add(100.1); t.Add(100.1); t.Add(100.1);
        Assert.False(t.IsDeparted);
    }

    [Fact]
    public void Status_never_claims_a_value_is_bad_only_that_it_is_unusual_here()
    {
        var t = Warm();
        for (var i = 0; i < 20; i++) t.Add(100 + (i % 3));
        for (var i = 0; i < 3; i++) t.Add(5);

        var status = t.Status(v => $"{v:0.0}/s");
        Assert.Contains("normal range", status);
        Assert.DoesNotContain("bad", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_reports_what_the_session_saw()
    {
        var t = Warm();
        foreach (var v in new double[] { 90, 100, 110 }) t.Add(v);
        var s = t.Describe();
        Assert.Equal(3, s.SampleCount);
        Assert.Equal(100, s.Median);
        Assert.Equal(90, s.Min);
        Assert.Equal(110, s.Max);
    }
}
