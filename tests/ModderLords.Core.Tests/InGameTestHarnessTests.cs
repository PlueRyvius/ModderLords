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
    [InlineData("hero-level")]
    [InlineData("both-perks")]
    [InlineData("parity")]
    public void The_built_in_suites_parse(string name)
    {
        var plan = TestSuites.Load(name);
        Assert.Equal(name, plan.Name);
        Assert.Equal(["server", "client:1"], plan.Sides);
        Assert.Equal(1, plan.Clients);
        Assert.False(plan.SinglePlayer);
    }

    [Fact]
    public void The_built_in_suites_are_grouped_into_test_modules_with_core_first()
    {
        var modules = TestSuites.Modules();
        Assert.Equal("core", modules[0].Id);
        Assert.Equal(["core", "parity", "perks", "retinues", "stash", "sync", "world"], modules.Select(m => m.Id));
        Assert.Equal(["hero-level", "sync-basics"], TestSuites.Module("sync")!.Suites.Select(s => s.Name));
        Assert.All(modules, m => Assert.All(m.Suites, s => Assert.Equal(m.Id, s.Module)));
        Assert.All(modules, m => Assert.False(string.IsNullOrWhiteSpace(m.Description)));
        Assert.Equal("sync", TestSuites.Load("sync/hero-level").Module);
        Assert.Equal("hero-level", TestSuites.Load("hero-level").Name);
        Assert.Contains("sync/sync-basics", TestSuites.Names());
    }

    [Fact]
    public void A_modules_requirements_are_added_to_each_of_its_suites()
    {
        var suite = TestPlan.Parse("""{ "api": 2, "name": "ammo", "mode": "singleplayer", "requires": [ "Harmony" ], "steps": [ { "id": "a", "side": "game" } ] }""");
        var module = TestModule.Parse("""{ "id": "smithing", "title": "Smithing", "requires": [ { "id": "ModularSmithing2", "version": ">=0.9.49" } ], "steps": "ModderLords.Tests.Smithing.dll" }""", [suite]);
        Assert.Equal([new ModRequirement("ModularSmithing2", ">=0.9.49"), new ModRequirement("Harmony")], module.Suites[0].Requires);
        Assert.Equal("ModderLords.Tests.Smithing.Steps", module.StepsType);
        Assert.Throws<FormatException>(() => TestModule.Parse("""{ "id": "x", "steps": "Steps.exe" }""", []));
    }

    [Fact]
    public void Picking_modules_runs_only_their_suites_and_core_including_v1_suites()
    {
        var mods = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var picked = SuiteSelection.Select(TestSuites.LoadAll(), mods, singlePlayer: false, onlyMod: null, withOthers: false, [], modules: ["sync"]);
        Assert.Equal(["core", "hero-level", "sync-basics"], picked.Select(p => p.Name));
        var none = SuiteSelection.Select(TestSuites.LoadAll(), mods, singlePlayer: false, onlyMod: null, withOthers: false, []);
        Assert.Equal(["core"], none.Select(p => p.Name));
        var notes = new List<string>();
        var single = SuiteSelection.Select(TestSuites.LoadAll(), mods, singlePlayer: true, onlyMod: null, withOthers: false, notes, modules: ["sync", "world"]);
        Assert.Equal(["core", "sp-world"], single.Select(p => p.Name));
        Assert.Contains("sync/hero-level: Coop only", notes);
    }

    [Fact]
    public void The_list_shows_every_module_and_whether_a_profile_can_run_it()
    {
        var suite = TestPlan.Parse("""{ "api": 2, "name": "ammo", "mode": "singleplayer", "steps": [ { "id": "smithing.craft", "side": "game" } ] }""");
        var smithing = TestModule.Parse("""{ "id": "smithing", "title": "Smithing", "description": "MS2", "requires": [ "ModularSmithing2" ] }""", [suite]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(TestCatalog.ToJson([.. TestSuites.Modules(), smithing], new Dictionary<string, string?>(), "P"))!;
        var listed = json["modules"]!.AsArray();
        Assert.Equal("core", listed[0]!["id"]!.GetValue<string>());
        Assert.True(listed[0]!["available"]!.GetValue<bool>());
        var ms2 = listed.Single(m => m!["id"]!.GetValue<string>() == "smithing")!;
        Assert.Equal("ModularSmithing2 (not in this profile)", ms2["available"]![0]!.GetValue<string>());
        Assert.Equal("smithing/ammo", ms2["suites"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("smithing.craft", ms2["suites"]![0]!["steps"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_core_suite_runs_both_ways()
    {
        var core = TestSuites.Load(SuiteSelection.CoreSuite);
        Assert.Equal(SuiteMode.Both, core.Mode);
        var single = core.ForMode(singlePlayer: true);
        Assert.Equal(["game"], single.Sides);
        Assert.DoesNotContain(single.Steps, s => s.IsCompare || s.Id == "hero.addGold");
        var coop = core.ForMode(singlePlayer: false);
        Assert.Equal(["server", "client:1"], coop.Sides);
        Assert.Contains(coop.Steps, s => s.IsCompare && s.Id == "hero.parity");
        Assert.Equal(2, coop.Steps.Count(s => s.Id == "mods.started"));
    }

    [Fact]
    public void Requirements_and_mode_are_read_from_a_v2_suite()
    {
        var plan = TestPlan.Parse("""
            { "api": 2, "name": "t", "mode": "coop", "continuity": true, "world": "shared",
              "requires": [ "CoopWarehouses", { "id": "ModularArmor2", "version": ">=1.2" } ],
              "steps": [ { "id": "a", "side": "server" } ] }
            """);
        Assert.Equal(SuiteMode.Coop, plan.Mode);
        Assert.True(plan.Continuity);
        Assert.True(plan.SharesWorld);
        Assert.Equal([new ModRequirement("CoopWarehouses"), new ModRequirement("ModularArmor2", ">=1.2")], plan.Requires);
    }

    [Theory]
    [InlineData("v1.2.3", ">=1.2", true)]
    [InlineData("v1.1.9", ">=1.2", false)]
    [InlineData("v1.2", "=1.2.0", true)]
    [InlineData("e1.4.8", "<2", true)]
    [InlineData("v2.0.0", "<2", false)]
    [InlineData("v1.3.0", "1.3", true)]
    [InlineData("v1.3.1", ">1.3", true)]
    public void Version_ranges_compare_number_by_number(string installed, string range, bool matches) =>
        Assert.Equal(matches, VersionRange.Matches(installed, range));

    private static TestPlan BothSuite(string name, string extra = "") =>
        TestPlan.Parse("{ \"api\": 2, \"name\": \"" + name + "\", \"mode\": \"both\"" + extra + ", \"steps\": [ { \"id\": \"a\", \"side\": \"game\" } ] }");

    [Fact]
    public void Selection_puts_core_first_and_keeps_only_suites_the_mods_meet()
    {
        var available = new[]
        {
            BothSuite("shared-one", ", \"world\": \"shared\""),
            BothSuite("needs-armor", ", \"requires\": [ { \"id\": \"ModularArmor2\", \"version\": \">=1.2\" } ]"),
            BothSuite("needs-old-armor", ", \"requires\": [ { \"id\": \"ModularArmor2\", \"version\": \"<1\" } ]"),
            BothSuite("needs-warehouses", ", \"requires\": [ \"CoopWarehouses\" ]"),
            TestPlan.Parse("""{ "api": 2, "name": "coop-only", "mode": "coop", "steps": [ { "id": "a", "side": "server" } ] }"""),
            TestPlan.Parse("""{ "name": "v1", "steps": [ { "id": "a", "side": "server" } ] }"""),
            TestSuites.Load(SuiteSelection.CoreSuite),
        };
        var mods = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ModularArmor2"] = "v1.3.0" };

        var notes = new List<string>();
        var picked = SuiteSelection.Select(available, mods, singlePlayer: true, onlyMod: null, withOthers: false, notes);
        Assert.Equal(["core", "needs-armor", "shared-one"], picked.Select(p => p.Name));
        Assert.Contains(notes, n => n.StartsWith("needs-old-armor: needs ModularArmor2 <1 (this profile has v1.3.0)", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith("needs-warehouses: needs CoopWarehouses (not in this profile)", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith("coop-only: Coop only", StringComparison.Ordinal));

        var onlyArmor = SuiteSelection.Select(available, mods, singlePlayer: true, onlyMod: "ModularArmor2", withOthers: false, []);
        Assert.Equal(["core", "needs-armor"], onlyArmor.Select(p => p.Name));
        var withOthers = SuiteSelection.Select(available, mods, singlePlayer: true, onlyMod: "ModularArmor2", withOthers: true, []);
        Assert.Equal(["core", "needs-armor", "shared-one"], withOthers.Select(p => p.Name));
    }

    [Fact]
    public void A_fresh_world_starts_a_Coop_launch_and_a_shared_one_joins_the_last()
    {
        TestPlan Suite(string name, bool shared) => TestPlan.Parse("{ \"api\": 2, \"name\": \"" + name + "\", \"mode\": \"coop\"" +
            (shared ? ", \"world\": \"shared\"" : "") + ", \"steps\": [ { \"id\": \"a\", \"side\": \"server\" } ] }");
        var launches = SuiteSelection.CoopLaunches([Suite("core", false), Suite("a", false), Suite("b", true), Suite("c", true)]);
        Assert.Equal([["core"], ["a", "b", "c"]], launches.Select(l => l.Select(p => p.Name).ToArray()));
    }

    [Fact]
    public void Composing_prefixes_names_and_references_and_adds_each_suites_setup()
    {
        var a = TestPlan.Parse("""{ "name": "a", "steps": [ { "id": "x", "side": "game", "as": "me" }, { "id": "y", "side": "game", "args": { "n": "$me.heroName" } } ] }""");
        var b = TestPlan.Parse("""{ "name": "b", "steps": [ { "id": "x", "side": "game", "as": "me" } ] }""");
        var plan = SuiteSelection.Compose("selected", [a, b], (suite, i) => SuiteSelection.SinglePlayerSetup(suite, i, world: null));
        Assert.Equal(["a/world", "a/me", "a/y", "b/leave", "b/world", "b/pause", "b/me", "end/noErrors.game"], plan.Steps.Select(s => s.Name));
        Assert.Equal("true", plan.Steps[^1].Args["new"]);
        Assert.Equal("$a/me.heroName", plan.Steps[2].Args["n"]);
        Assert.Equal(SuiteSelection.RunWorld, plan.Steps[0].Args["name"]);
        Assert.Equal("game.createWorld", plan.Steps[0].Id);
        Assert.Equal("game.loadSave", plan.Steps[4].Id);
        Assert.All(plan.Steps.Take(3), s => Assert.Equal("a", s.Suite));
    }

    [Fact]
    public void Every_suites_error_patterns_reach_every_error_check()
    {
        var a = TestPlan.Parse("""{ "api": 2, "name": "a", "mode": "coop", "errorPatterns": [ "\\[MSmith\\].*failed" ], "steps": [ { "id": "game.noErrors", "side": "server" } ] }""");
        var b = TestPlan.Parse("""{ "api": 2, "name": "b", "mode": "coop", "errorPatterns": [ "Armor broke" ], "steps": [ { "id": "x", "side": "client:1" } ] }""");
        var plan = SuiteSelection.Compose("selected", [a, b], (_, _) => []);
        var checks = plan.Steps.Where(s => s.Id == "game.noErrors").ToList();
        Assert.Equal(["a/game.noErrors", "end/noErrors.server", "end/noErrors.client:1"], checks.Select(s => s.Name));
        Assert.All(checks, s => Assert.Equal("""["\\[MSmith\\].*failed","Armor broke"]""", s.Args["patterns"]));
    }

    [Fact]
    public void A_bad_error_pattern_is_caught_when_the_suite_loads()
    {
        var ex = Assert.Throws<FormatException>(() => TestPlan.Parse("""{ "api": 2, "name": "t", "errorPatterns": [ "([" ], "steps": [ { "id": "a", "side": "server" } ] }"""));
        Assert.Contains("not a regular expression", ex.Message);
    }

    [Theory]
    [InlineData("[MSmith] catalog load failed: stat 'ammo.missile_speed' not allowed", "", true)]
    [InlineData("Exception while loading the armour catalog", "", true)]
    [InlineData("You have gained 5 renown", "", false)]
    [InlineData("Unfailing loyalty", "", false)]
    [InlineData("Armor broke in two", "[\"Armor broke\"]", true)]
    [InlineData("Armor broke in two", "Armor broke", true)]
    public void On_screen_errors_match_the_default_words_and_a_suites_own_patterns(string message, string extra, bool isError) =>
        Assert.Equal(isError, ModderLords.Testing.ErrorPatterns.Matches([message], ModderLords.Testing.ErrorPatterns.From(extra)).Count == 1);

    [Fact]
    public void A_single_player_suite_runs_on_the_game_alone()
    {
        var plan = TestSuites.Load("sp-world");
        Assert.Equal(["game"], plan.Sides);
        Assert.True(plan.SinglePlayer);
        Assert.Equal(0, plan.Clients);
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
    [InlineData("""{ "api": 3, "name": "t", "steps": [ { "id": "a", "side": "server" } ] }""", "v3")]
    [InlineData("""{ "name": "t", "steps": [] }""", "no steps")]
    [InlineData("""{ "name": "t", "steps": [ { "side": "server" } ] }""", "neither")]
    [InlineData("""{ "name": "t", "steps": [ { "id": "a", "side": "game" }, { "id": "b", "side": "server" } ] }""", "one or the other")]
    [InlineData("""{ "name": "t", "mode": "coop", "steps": [ { "id": "a", "side": "server" } ] }""", "need \"api\": 2")]
    [InlineData("""{ "api": 2, "name": "t", "mode": "both", "steps": [ { "id": "a", "side": "server" } ] }""", "names \"game\" and \"player\"")]
    [InlineData("""{ "api": 2, "name": "t", "mode": "coop", "steps": [ { "id": "a", "side": "player" } ] }""", "not \"player\"")]
    [InlineData("""{ "api": 2, "name": "t", "mode": "coop", "steps": [ { "id": "a", "side": "server", "mode": "coop" } ] }""", "belongs to a suite")]
    [InlineData("""{ "api": 2, "name": "t", "mode": "sometimes", "steps": [ { "id": "a", "side": "server" } ] }""", "singleplayer, coop or both")]
    [InlineData("""{ "api": 2, "name": "t", "requires": [ { "id": "X", "version": ">=abc" } ], "steps": [ { "id": "a", "side": "server" } ] }""", "not a version")]
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
    private readonly Dictionary<string, int> _stash = new() { ["server"] = 0, ["client:1"] = 0, ["game"] = 0 };

    public TestSequencerTests()
    {
        InGameTests.Clear();
        InGameTests.Register("stash.deposit", StepSide.Client, ctx => Do(() => { _stash["server"] += ctx.ArgInt("count"); _stash[ctx.Side] += ctx.ArgInt("count"); ctx.Report("value", _stash[ctx.Side]); }));
        InGameTests.Register("stash.count", StepSide.Any, ctx => Do(() => ctx.Report("value", _stash[ctx.Side])));
        InGameTests.Register("stash.double", StepSide.Client, ctx => Do(() => _stash[ctx.Side] *= 2));
        InGameTests.Register("check", StepSide.Any, ctx => Do(() => ctx.Assert(ctx.Arg("got") == ctx.Arg("want"), $"got {ctx.Arg("got")}, want {ctx.Arg("want")}")));
        InGameTests.Register("fail", StepSide.Any, ctx => Do(() => ctx.Assert(false, "broken upstream")));
        InGameTests.Register("hang", StepSide.Any, ctx => Hang(ctx));
        InGameTests.Register("game.noErrors", StepSide.Any, ctx => Do(() => ctx.Report("value", "none")));
    }

    public void Dispose()
    {
        InGameTests.Clear();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_control_line_read_in_the_middle_of_a_character_keeps_the_character()
    {
        // The agent can read control.jsonl while the launcher is still writing a line; a read ending between the two
        // bytes of 'é' must not turn it into replacement characters.
        InGameTests.Register("echo", StepSide.Any, ctx => Do(() => ctx.Report("value", ctx.Arg("name"))));
        var folder = Directory.CreateDirectory(Path.Combine(_root, "split")).FullName;
        File.WriteAllText(Path.Combine(folder, "run.json"), """{"api":1,"runId":"r","suite":"t","token":"tok"}""");
        var control = Path.Combine(folder, "control.jsonl");
        File.WriteAllText(control, "");
        var agent = new TestAgent(TestRunSwitch.Parse(folder + "|server|tok")!, _ => { });
        var line = System.Text.Encoding.UTF8.GetBytes("""{"op":"step","seq":1,"side":"server","step":"echo","args":{"name":"Renée"},"timeoutMs":5000}""" + "\n");
        var cut = Array.IndexOf(line, (byte)0xC3) + 1;   // inside the two bytes of 'é'

        using (var s = new FileStream(control, FileMode.Append)) s.Write(line, 0, cut);
        agent.Tick(DateTime.UtcNow);
        using (var s = new FileStream(control, FileMode.Append)) s.Write(line, cut, line.Length - cut);
        for (int i = 0; i < 5; i++) agent.Tick(DateTime.UtcNow);

        var result = File.ReadAllLines(Path.Combine(folder, TestAgent.ResultsFile("server"))).Last();
        Assert.Contains("\"value\":\"Renée\"", result);
    }

    [Fact]
    public async Task A_single_player_game_runs_server_and_client_steps_alike()
    {
        // The game is both the world and the player: a Server step and a Client step both run there.
        InGameTests.Register("world.change", StepSide.Server, ctx => Do(() => ctx.Report("value", "changed")));
        var report = await Run("""
            { "name": "t", "steps": [
              { "id": "world.change", "side": "game", "as": "change" },
              { "id": "stash.double", "side": "game" },
              { "id": "check", "side": "game", "args": { "got": "$change.value", "want": "changed" } } ] }
            """);
        Assert.True(report.Passed, report.ToText());
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
    public async Task A_failure_skips_the_rest_of_its_suite_and_the_next_suite_still_runs()
    {
        var a = TestPlan.Parse("""{ "name": "a", "steps": [ { "id": "fail", "side": "server" }, { "id": "stash.count", "side": "server" } ] }""");
        var b = TestPlan.Parse("""{ "name": "b", "steps": [ { "id": "stash.count", "side": "server" } ] }""");
        var report = await Run(SuiteSelection.Compose("selected", [a, b], (_, _) => []));
        // The closing on-screen error check runs last, whatever failed before it.
        Assert.Equal([CoreOutcome.Fail, CoreOutcome.Skipped, CoreOutcome.Pass, CoreOutcome.Pass], report.Steps.Select(s => s.Outcome));
        Assert.Equal([("a", false), ("b", true), ("end", true)], report.Suites);
        Assert.Contains("suites: a FAIL, b PASS, end PASS", report.ToText());
    }

    [Fact]
    public async Task A_module_dll_is_skipped_on_a_side_without_its_mod()
    {
        var plan = TestPlan.Parse("""{ "name": "t", "steps": [ { "id": "stash.count", "side": "server" } ] }""")
            with { Loads = [new ModuleLoad("smithing", Path.Combine(_root, "missing.dll"), "ModderLords.Tests.Smithing.Steps", ["ModularSmithing2"])] };
        var report = await Run(plan);
        Assert.True(report.Passed, report.ToText());
        Assert.Contains("server: test module smithing not loaded here: this game has no ModularSmithing2", report.Notes);
    }

    [Fact]
    public async Task A_module_dll_that_will_not_load_stops_the_run_before_any_step()
    {
        var plan = TestPlan.Parse("""{ "name": "t", "steps": [ { "id": "stash.count", "side": "server" } ] }""")
            with { Loads = [new ModuleLoad("smithing", Path.Combine(_root, "missing.dll"), "ModderLords.Tests.Smithing.Steps", [])] };
        var report = await Run(plan);
        Assert.Equal(CoreOutcome.Skipped, report.Steps[0].Outcome);
        Assert.Contains(report.Notes, n => n.StartsWith("server could not load a test module: smithing: missing.dll", StringComparison.Ordinal));
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
    [InlineData(new[] { "/modderlords-test", "C:\\runs\\r1|game|abc" }, null, false, "game")]
    [InlineData(new string[0], "C:\\runs\\r1|server|abc", false, null)]     // the environment counts on the server only
    [InlineData(new[] { "/modderlords-test", "C:\\runs\\r1|client|abc" }, null, false, null)]
    [InlineData(new[] { "/modderlords-test", "C:\\runs\\r1|client:1" }, null, false, null)]
    public void The_switch_is_read_from_the_right_place(string[] args, string? env, bool isServer, string? side)
    {
        Assert.Equal(side, TestRunSwitch.From(args, env, isServer)?.Side);
    }

    private Task<TestReport> Run(string suite, bool stopAgents = false, string? token = null) => Run(TestPlan.Parse(suite), stopAgents, token);

    private async Task<TestReport> Run(TestPlan plan, bool stopAgents = false, string? token = null)
    {
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
