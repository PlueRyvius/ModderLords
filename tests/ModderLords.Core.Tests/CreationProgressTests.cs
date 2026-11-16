using ModderLords.Coop.Launch;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>The Host tab's world creation progress bar: phases are the facts, the last run's timings the pace.</summary>
public sealed class CreationProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 15, 52, 35, TimeSpan.Zero);

    [Fact]
    public void Each_phase_moves_the_bar_to_its_mark_and_saved_fills_it()
    {
        var p = new CreationProgress();
        p.Start(T0);
        Assert.Equal(0, p.Fraction(T0));
        p.Reached("armed", T0.AddSeconds(6));
        Assert.Equal(p.Marks[0], p.Fraction(T0.AddSeconds(6)), 6);
        p.Reached("map-ready", T0.AddSeconds(125));
        Assert.Equal("map-ready", p.Phase);
        Assert.Equal(p.Marks[3], p.Fraction(T0.AddSeconds(125)), 6);
        p.Reached("saved", T0.AddSeconds(139));
        Assert.Equal(1, p.Fraction(T0.AddSeconds(139)));
    }

    [Fact]
    public void Between_phases_it_fills_at_the_expected_pace_but_never_reaches_the_next_mark()
    {
        var p = new CreationProgress();
        p.Start(T0);
        p.Reached("armed", T0.AddSeconds(6));
        var halfway = p.Fraction(T0.AddSeconds(6 + 20.5));   // "starting" is expected 41 s after "armed"
        Assert.Equal(p.Marks[0] + (p.Marks[1] - p.Marks[0]) * 0.5, halfway, 6);
        // A slow run: ten minutes on, the bar still waits short of "starting".
        var slow = p.Fraction(T0.AddMinutes(10));
        Assert.True(slow < p.Marks[1]);
        Assert.Equal(p.Marks[0] + (p.Marks[1] - p.Marks[0]) * CreationProgress.Cap, slow, 6);
    }

    [Fact]
    public void Unknown_or_out_of_order_phases_change_nothing()
    {
        var p = new CreationProgress();
        p.Start(T0);
        p.Reached("campaign-created", T0.AddSeconds(90));
        p.Reached("armed", T0.AddSeconds(95));
        p.Reached("something-new", T0.AddSeconds(96));
        Assert.Equal("campaign-created", p.Phase);
    }

    [Fact]
    public void A_finished_run_gives_its_step_timings_and_the_next_run_is_paced_by_them()
    {
        var p = new CreationProgress();
        p.Start(T0);
        double[] at = [6, 47, 88, 125, 125.02, 139];
        for (var i = 0; i < at.Length; i++) p.Reached(CreationProgress.Phases[i], T0.AddSeconds(at[i]));
        Assert.Equal([6, 41, 41, 37, 0, 14], p.Durations()!);

        // A quick mod set: the marks follow its own timings.
        var next = new CreationProgress([2, 10, 10, 10, 0, 8]);
        Assert.Equal(2 / 40.5, next.Marks[0], 3);
        Assert.Equal(1, next.Marks[5], 6);
        Assert.Null(new CreationProgress().Durations());
    }

    [Fact]
    public void Timings_are_kept_per_profile_and_a_broken_file_means_the_defaults()
    {
        var file = Path.Combine(Path.GetTempPath(), "ml-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(CreationTimes.Load("TAOM", file));
            CreationTimes.Save("TAOM", [6, 41, 41, 37, 0, 14], file);
            CreationTimes.Save("Vanilla", [1, 5, 5, 5, 0, 3], file);
            Assert.Equal([6, 41, 41, 37, 0, 14], CreationTimes.Load("TAOM", file)!);
            File.WriteAllText(file, "{ not json");
            Assert.Null(CreationTimes.Load("TAOM", file));
            Assert.Equal(new CreationProgress().Marks, new CreationProgress(CreationTimes.Load("TAOM", file)).Marks);
        }
        finally { File.Delete(file); }
    }
}
