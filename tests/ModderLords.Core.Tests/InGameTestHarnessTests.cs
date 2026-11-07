using ModderLords.Core.Testing;
using ModderLords.Testing;
using Xunit;
using CoreOutcome = ModderLords.Core.Testing.StepOutcome;

namespace ModderLords.Core.Tests;

/// <summary>The suite format: the built-in suites parse, and a mistake is caught before a game starts.</summary>
public class TestPlanTests
{
    [Theory]
    [InlineData("stash")]
    [InlineData("sync-basics")]
    public void The_built_in_suites_parse(string name)
    {
        var plan = TestSuites.Load(name);
        Assert.Equal(name, plan.Name);
        Assert.Equal(["server", "client:1"], plan.Sides);
        Assert.Equal(1, plan.Clients);
    }

    [Fact]
    public void Numbers_in_args_travel_as_text_and_references_are_kept()
    {
        var plan = TestPlan.Parse("""
            { "name": "t", "steps": [
              { "id": "a", "side": "server", "args": { "n": 5, "flag": true } },
              { "id": "b", "side": "client:1", "args": { "x": "$a.value" } } ] }
            """);
        Assert.Equal("5", plan.Steps[0].Args["n"]);
        Assert.Equal("true", plan.Steps[0].Args["flag"]);
        Assert.Equal("$a.value", plan.Steps[1].Args["x"]);
    }

    [Theory]
    [InlineData("""{ "name": "t", "steps": [ { "id": "a", "side": "client" } ] }""", "client:N")]
    [InlineData("""{ "name": "t", "steps": [ { "compare": "a", "sides": ["server"] } ] }""", "two or more")]
    [InlineData("""{ "name": "t", "steps": [ { "id": "a", "side": "server", "args": { "x": "$b.value" } } ] }""", "not an earlier step")]
    [InlineData("""{ "api": 2, "name": "t", "steps": [ { "id": "a", "side": "server" } ] }""", "v2")]
    [InlineData("""{ "name": "t", "steps": [] }""", "no steps")]
    [InlineData("""{ "name": "t", "steps": [ { "side": "server" } ] }""", "neither")]
    public void A_bad_suite_names_its_problem(string json, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => TestPlan.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("$me.hero", "me", "hero")]
    [InlineData("$stash.deposit.value", "stash.deposit", "value")]
    public void A_reference_splits_at_the_last_dot(string value, string step, string key)
    {
        Assert.Equal((step, key), TestPlan.Reference(value));
    }

    [Theory]
    [InlineData("hero")]
    [InlineData("$nodot")]
    [InlineData("$a.")]
    public void Plain_values_are_not_references(string value) => Assert.Null(TestPlan.Reference(value));
}

/// <summary>The waits a step yields, against a clock the test moves.</summary>
public class StepRunnerTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static StepRunner Runner(StepBody body, TimeSpan? timeout = null) =>
        new(body, new StepContext("s", "client:1", new Dictionary<string, string> { ["n"] = "3" }, new Dictionary<string, object?>(), (_, _) => { }),
            T0, timeout ?? TimeSpan.FromSeconds(60));

    [Fact]
    public void A_step_that_returns_passes_and_keeps_its_values()
    {
        var runner = Runner(ctx => Once(() => ctx.Report("value", ctx.ArgInt("n") * 2)));
        var end = Run(runner, 1);
        Assert.True(end!.Passed);
        Assert.Equal("6", runner.Context.Values["value"]);
    }

    [Fact]
    public void A_failed_assert_fails_with_its_message_alone()
    {
        var end = Run(Runner(ctx => Once(() => ctx.Assert(false, "stash holds 10, expected 5"))), 1);
        Assert.False(end!.Passed);
        Assert.Equal("stash holds 10, expected 5", end.Message);
    }

    [Fact]
    public void An_exception_fails_the_step_with_its_type()
    {
        var end = Run(Runner(_ => Once(() => throw new InvalidOperationException("boom"))), 1);
        Assert.Equal("InvalidOperationException: boom", end!.Message);
    }

    [Fact]
    public void A_missing_argument_fails_by_name()
    {
        var end = Run(Runner(ctx => Once(() => ctx.Arg("town"))), 1);
        Assert.Contains("'town'", end!.Message);
    }

    [Fact]
    public void Until_resumes_when_the_condition_holds_and_fails_on_its_own_timeout()
    {
        var ready = false;
        var runner = Runner(ctx => Yield(ctx.Until(() => ready, "the map", TimeSpan.FromSeconds(10))));
        Assert.Null(runner.Tick(T0));
        Assert.Null(runner.Tick(T0.AddSeconds(5)));
        ready = true;
        Assert.True(Tick(runner, T0.AddSeconds(6))!.Passed);

        var never = Runner(ctx => Yield(ctx.Until(() => false, "the map", TimeSpan.FromSeconds(10))));
        never.Tick(T0);
        Assert.Null(never.Tick(T0.AddSeconds(10)));
        Assert.Equal("timed out waiting for the map", never.Tick(T0.AddSeconds(11))!.Message);
    }

    [Fact]
    public void Settled_waits_until_the_value_has_stopped_changing()
    {
        var value = 0;
        var runner = Runner(ctx => Yield(ctx.Settled(() => value, "the stash", quiet: TimeSpan.FromSeconds(2))));
        runner.Tick(T0);
        Assert.Null(runner.Tick(T0.AddSeconds(1.5)));
        value = 5;                                              // a sync still arriving restarts the quiet period
        Assert.Null(runner.Tick(T0.AddSeconds(2.5)));
        Assert.Null(runner.Tick(T0.AddSeconds(4)));
        Assert.True(Tick(runner, T0.AddSeconds(4.6))!.Passed);
    }

    [Fact]
    public void Settled_with_an_expected_value_waits_for_that_value_then_quiet()
    {
        var value = 10;
        var runner = Runner(ctx => Yield(ctx.Settled(() => value, "the stash", expected: 5, timeout: TimeSpan.FromSeconds(30))));
        runner.Tick(T0);
        Assert.Null(runner.Tick(T0.AddSeconds(5)));             // stable, but wrong
        value = 5;
        Assert.Null(runner.Tick(T0.AddSeconds(6)));
        Assert.True(Tick(runner, T0.AddSeconds(8.1))!.Passed);

        var wrong = Runner(ctx => Yield(ctx.Settled(() => 10, "the stash", expected: 5, timeout: TimeSpan.FromSeconds(30))));
        wrong.Tick(T0);
        Assert.Equal("timed out waiting for the stash to be 5", wrong.Tick(T0.AddSeconds(31))!.Message);
    }

    [Fact]
    public void The_steps_own_timeout_ends_any_wait()
    {
        var runner = Runner(ctx => Yield(ctx.Until(() => false, "a reply")), TimeSpan.FromSeconds(5));
        runner.Tick(T0);
        Assert.Equal("the step timed out waiting for a reply", runner.Tick(T0.AddSeconds(6))!.Message);
    }

    [Fact]
    public void Yielding_null_waits_exactly_one_tick()
    {
        var steps = 0;
        var runner = Runner(_ => Frames(() => steps++));
        Assert.Null(runner.Tick(T0));
        Assert.Equal(1, steps);
        Assert.Null(runner.Tick(T0));
        Assert.Equal(2, steps);
    }

    private static IEnumerable<StepWait?> Once(Action action) { action(); yield break; }
    private static IEnumerable<StepWait?> Yield(StepWait wait) { yield return wait; }
    private static IEnumerable<StepWait?> Frames(Action each) { while (true) { each(); yield return null; } }

    private static StepEnd? Run(StepRunner runner, int ticks)
    {
        StepEnd? end = null;
        for (int i = 0; i < ticks + 1 && end is null; i++) end = runner.Tick(T0);
        return end;
    }

    /// <summary>Ticks once at <paramref name="now"/>, and once more if that resumed the step (which then returns).</summary>
    private static StepEnd? Tick(StepRunner runner, DateTime now) => runner.Tick(now) ?? runner.Tick(now);
}

/// <summary>
/// The launcher's sequencer against the real in-game agent, through a real run folder: both ends of the file protocol,
/// with no game. The registry is static, so these run one at a time.
/// </summary>
[Collection(nameof(InGameTests))]
public sealed class TestSequencerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-test-harness-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _stash = new() { ["server"] = 0, ["client:1"] = 0 };

    public TestSequencerTests()
    {
        InGameTests.Clear();
        InGameTests.Register("stash.deposit", StepSide.Client, ctx => Do(() => { _stash["server"] += ctx.ArgInt("count"); _stash[ctx.Side] += ctx.ArgInt("count"); ctx.Report("value", _stash[ctx.Side]); }));
        InGameTests.Register("stash.count", StepSide.Any, ctx => Do(() => ctx.Report("value", _stash[ctx.Side])));
        InGameTests.Register("stash.double", StepSide.Client, ctx => Do(() => _stash[ctx.Side] *= 2));
        InGameTests.Register("check", StepSide.Any, ctx => Do(() => ctx.Assert(ctx.Arg("got") == ctx.Arg("want"), $"got {ctx.Arg("got")}, want {ctx.Arg("want")}")));
        InGameTests.Register("fail", StepSide.Any, ctx => Do(() => ctx.Assert(false, "broken upstream")));
        InGameTests.Register("hang", StepSide.Any, ctx => Hang(ctx));
    }

    public void Dispose()
    {
        InGameTests.Clear();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Values_flow_between_sides_and_a_compare_passes_when_both_agree()
    {
        var report = await Run("""
            { "name": "t", "steps": [
              { "id": "stash.deposit", "side": "client:1", "as": "deposit", "args": { "count": 5 } },
              { "id": "check", "side": "server", "args": { "got": "$deposit.value", "want": "5" } },
              { "compare": "stash.count", "sides": ["server", "client:1"] } ] }
            """);
        Assert.True(report.Passed, report.ToText());
        Assert.Equal("all read 5", report.Steps[2].Message);
        Assert.Equal("5", report.Steps[2].Values["value@server"]);
    }

    [Fact]
    public async Task A_compare_fails_when_the_sides_disagree_and_later_steps_are_skipped()
    {
        var report = await Run("""
            { "name": "t", "steps": [
              { "id": "stash.deposit", "side": "client:1", "args": { "count": 5 } },
              { "id": "stash.double", "side": "client:1" },
              { "compare": "stash.count", "sides": ["server", "client:1"] },
              { "id": "stash.count", "side": "server" } ] }
            """);
        Assert.False(report.Passed);
        Assert.Equal(CoreOutcome.Fail, report.Steps[2].Outcome);
        Assert.Equal("the sides disagree: server 5, client:1 10", report.Steps[2].Message);
        Assert.Equal(CoreOutcome.Skipped, report.Steps[3].Outcome);
    }

    [Fact]
    public async Task Known_failures_do_not_fail_the_run_and_unexpected_passes_do()
    {
        var known = await Run("""
            { "name": "t", "steps": [
              { "id": "fail", "side": "server", "knownFailing": "https://example.invalid/1" },
              { "id": "stash.count", "side": "server" } ] }
            """);
        Assert.True(known.Passed, known.ToText());
        Assert.Equal(CoreOutcome.KnownFail, known.Steps[0].Outcome);
        Assert.Equal(CoreOutcome.Pass, known.Steps[1].Outcome);

        var fixedUpstream = await Run("""{ "name": "t", "steps": [ { "id": "stash.count", "side": "server", "knownFailing": "https://example.invalid/1" } ] }""");
        Assert.False(fixedUpstream.Passed);
        Assert.Equal(CoreOutcome.UnexpectedPass, fixedUpstream.Steps[0].Outcome);
    }

    [Fact]
    public async Task ContinueOnFail_runs_the_next_step()
    {
        var report = await Run("""
            { "name": "t", "steps": [
              { "id": "fail", "side": "client:1", "continueOnFail": true },
              { "id": "stash.count", "side": "client:1" } ] }
            """);
        Assert.Equal(CoreOutcome.Fail, report.Steps[0].Outcome);
        Assert.Equal("broken upstream", report.Steps[0].Message);
        Assert.Equal(CoreOutcome.Pass, report.Steps[1].Outcome);
    }

    [Fact]
    public async Task A_step_no_game_has_is_found_before_anything_runs()
    {
        var report = await Run("""
            { "name": "t", "steps": [
              { "id": "stash.deposit", "side": "client:1", "args": { "count": 5 } },
              { "id": "stash.deposit", "side": "server", "args": { "count": 5 } },
              { "id": "nope", "side": "server" } ] }
            """);
        Assert.All(report.Steps, s => Assert.Equal(CoreOutcome.Skipped, s.Outcome));
        Assert.Contains("stash.deposit on server, nope on server", report.Notes[0]);
        Assert.Equal(0, _stash["client:1"]);
    }

    [Fact]
    public async Task A_game_that_stops_answering_is_reported_as_no_result()
    {
        var report = await Run("""{ "name": "t", "steps": [ { "id": "hang", "side": "server", "timeoutSeconds": 0.2 } ] }""", stopAgents: true);
        Assert.Equal(CoreOutcome.NoResult, report.Steps[0].Outcome);
        Assert.Contains("crashed or hung", report.Steps[0].Message);
    }

    [Fact]
    public async Task A_step_the_game_times_out_is_a_failure_not_a_missing_result()
    {
        var report = await Run("""{ "name": "t", "steps": [ { "id": "hang", "side": "client:1", "timeoutSeconds": 0.3 } ] }""");
        Assert.Equal(CoreOutcome.Fail, report.Steps[0].Outcome);
        Assert.Contains("timed out", report.Steps[0].Message);
    }

    [Fact]
    public async Task A_game_started_for_another_run_refuses_it()
    {
        var report = await Run("""{ "name": "t", "steps": [ { "id": "stash.count", "side": "server" } ] }""", token: "not-the-token");
        Assert.All(report.Steps, s => Assert.Equal(CoreOutcome.Skipped, s.Outcome));
        Assert.Contains("refused the run", report.Notes[0]);
    }

    [Theory]
    [InlineData(new[] { "/autoconnect", "127.0.0.1:4200", "/modderlords-test", "C:\\runs\\r1|client:1|abc" }, null, false, "client:1")]
    [InlineData(new string[0], "C:\\runs\\r1|server|abc", true, "server")]
    [InlineData(new string[0], "C:\\runs\\r1|server|abc", false, null)]     // the environment counts on the server only
    [InlineData(new[] { "/modderlords-test", "C:\\runs\\r1|client|abc" }, null, false, null)]
    [InlineData(new[] { "/modderlords-test", "C:\\runs\\r1|client:1" }, null, false, null)]
    public void The_switch_is_read_from_the_right_place(string[] args, string? env, bool isServer, string? side)
    {
        Assert.Equal(side, TestRunSwitch.From(args, env, isServer)?.Side);
    }

    private async Task<TestReport> Run(string suite, bool stopAgents = false, string? token = null)
    {
        var plan = TestPlan.Parse(suite);
        var channel = FileTestRunChannel.Create(_root, plan, DateTimeOffset.Now);
        var agents = plan.Sides.Select(side => new TestAgent(
            TestRunSwitch.Parse(token is null ? channel.SwitchValue(side) : $"{channel.Folder}|{side}|{token}")!, _ => { })).ToList();
        using var stop = new CancellationTokenSource();
        var game = Task.Run(async () =>
        {
            var hellos = 0;
            while (!stop.IsCancellationRequested)
            {
                foreach (var agent in agents) agent.Tick(DateTime.UtcNow);
                // The "crashed" game: answers hello, then never ticks again.
                if (stopAgents && ++hellos > 1) return;
                await Task.Delay(5);
            }
        });
        var sequencer = new TestSequencer(channel, new TestSequencerOptions
        {
            HelloTimeout = TimeSpan.FromSeconds(5),
            Grace = TimeSpan.FromSeconds(0.5),
            Poll = TimeSpan.FromMilliseconds(10),
        });
        var report = await sequencer.RunAsync(plan, channel.RunId, CancellationToken.None);
        stop.Cancel();
        await game;
        return report;
    }

    private static IEnumerable<StepWait?> Do(Action action) { action(); yield break; }
    private static IEnumerable<StepWait?> Hang(StepContext ctx) { yield return ctx.Until(() => false, "forever"); }
}
