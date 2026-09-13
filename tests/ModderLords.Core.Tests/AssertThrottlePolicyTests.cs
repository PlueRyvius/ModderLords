using ModderLords.Compat;

namespace ModderLords.Core.Tests;

/// <summary>
/// Pins the assert throttle. The live failure: ~7,300 identical pathfinding asserts a second filled the server's engine
/// error log at 1.2 MB/s and cut its tick rate from 63/s to 15.5/s.
/// </summary>
public class AssertThrottlePolicyTests
{
    private const string Path = "MobileParty.cs:3907 ComputePath";

    [Fact]
    public void The_first_occurrences_of_a_site_are_all_forwarded()
    {
        var p = new AssertThrottlePolicy();
        for (var i = 1; i <= AssertThrottlePolicy.ForwardFirst; i++)
            Assert.True(p.ShouldForward(Path, out _), $"occurrence {i}");
    }

    [Fact]
    public void Repeats_after_that_are_held_back_except_every_nth()
    {
        var p = new AssertThrottlePolicy();
        var forwarded = 0;
        for (var i = 0; i < 2 * AssertThrottlePolicy.ForwardEvery; i++)
            if (p.ShouldForward(Path, out _)) forwarded++;

        Assert.Equal(AssertThrottlePolicy.ForwardFirst + 2, forwarded);
    }

    [Fact]
    public void A_new_site_is_never_held_back_by_another_sites_storm()
    {
        var p = new AssertThrottlePolicy();
        for (var i = 0; i < 50_000; i++) p.ShouldForward(Path, out _);

        Assert.True(p.ShouldForward("Other.cs:12 Method", out var total));
        Assert.Equal(1, total);
    }

    [Fact]
    public void The_summary_names_the_busiest_site_and_resets_each_period()
    {
        var p = new AssertThrottlePolicy();
        Assert.Null(p.TakeSummary());

        for (var i = 0; i < 1_020; i++) p.ShouldForward(Path, out _);
        for (var i = 0; i < 25; i++) p.ShouldForward("Quiet.cs:1 M", out _);

        var summary = p.TakeSummary();
        Assert.NotNull(summary);
        Assert.StartsWith("repeated asserts held back from the engine log: " + Path + " x1,000 (total 1,020)", summary);
        Assert.Contains("Quiet.cs:1 M x5", summary);
        Assert.Null(p.TakeSummary());
    }

    [Fact]
    public void The_key_is_file_line_and_method()
        => Assert.Equal("MobileParty.cs:3907 ComputePath", AssertThrottlePolicy.KeyFor("MobileParty.cs", "ComputePath", 3907));
}
