using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

public class RepeatCollapserTests
{
    // The real line, from a healthy TAOM session that logged 10,827 of them in 53,273.
    private static string Failure(int id) =>
        $"[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.Hero\" id {id}";

    [Fact]
    public void Shows_the_first_few_then_says_it_is_collapsing()
    {
        var c = new RepeatCollapser(showFirst: 3, summariseEvery: 500);
        for (var i = 0; i < 3; i++) Assert.Equal(Failure(i), c.Filter(Failure(i)));
        Assert.Contains("counted, not shown", c.Filter(Failure(4)));
        Assert.Null(c.Filter(Failure(5)));
    }

    [Fact]
    public void Reports_periodically_so_a_long_run_is_not_silent()
    {
        var c = new RepeatCollapser(showFirst: 3, summariseEvery: 10);
        string? tenth = null;
        for (var i = 0; i < 10; i++) tenth = c.Filter(Failure(i));
        Assert.Contains("still repeating (10 times)", tenth);
    }

    [Fact]
    public void Groups_lines_that_differ_only_by_an_id()
    {
        // 7,448 of these differed only by which hero they named; as distinct messages nothing would collapse.
        var c = new RepeatCollapser(showFirst: 1, summariseEvery: 1000);
        Assert.NotNull(c.Filter(Failure(1)));
        Assert.Contains("counted, not shown", c.Filter(Failure(99999)));
        Assert.Null(c.Filter(Failure(12345)));
    }

    [Fact]
    public void Different_messages_are_collapsed_independently()
    {
        var c = new RepeatCollapser(showFirst: 1, summariseEvery: 1000);
        Assert.NotNull(c.Filter("first kind of problem"));
        Assert.NotNull(c.Filter("second kind of problem"));
        Assert.Contains("counted", c.Filter("first kind of problem"));
        Assert.Contains("counted", c.Filter("second kind of problem"));
    }

    [Fact]
    public void An_ordinary_varied_log_passes_through_untouched()
    {
        var c = new RepeatCollapser();
        foreach (var line in new[] { "loading module A", "loading module B", "SERVING", "player joined" })
            Assert.Equal(line, c.Filter(line));
    }

    [Fact]
    public void Totals_report_what_was_collapsed_most_first()
    {
        var c = new RepeatCollapser(showFirst: 1, summariseEvery: 10_000);
        for (var i = 0; i < 50; i++) c.Filter(Failure(i));
        for (var i = 0; i < 5; i++) c.Filter("something else entirely");

        var totals = c.Totals.ToList();
        Assert.Equal(50, totals[0].Value);
        Assert.Contains("ObjectManager", totals[0].Key);
        Assert.Equal(5, totals[1].Value);
    }

    [Fact]
    public void Totals_leave_out_anything_that_never_had_to_be_collapsed()
    {
        var c = new RepeatCollapser(showFirst: 3);
        c.Filter("said once");
        Assert.Empty(c.Totals);
    }

    [Fact]
    public void A_very_long_line_is_trimmed_in_the_summary()
    {
        var c = new RepeatCollapser(showFirst: 1, summariseEvery: 1000);
        var long_ = new string('x', 500);
        c.Filter(long_);
        var summary = c.Filter(long_);
        Assert.NotNull(summary);
        Assert.True(summary!.Length < 200, "a collapsed summary must not itself be a wall of text");
    }
}
