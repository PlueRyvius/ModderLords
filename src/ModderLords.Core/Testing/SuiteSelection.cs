namespace ModderLords.Core.Testing;

/// <summary>
/// Which suites a run with no suite named runs (docs/INGAME-TEST-HARNESS.md, decisions 11–13, 19 and 21), and how
/// they become one plan: the core suite first, then every suite whose mode fits the run and whose requirements the
/// profile's mods meet. Suites that opted in to sharing a world run straight after core, on core's world; the rest each
/// get a fresh one.
/// </summary>
public static class SuiteSelection
{
    public const string CoreSuite = "core";

    private const string NoErrorsStep = "game.noErrors";

    /// <summary>The patterns as the JSON array game.noErrors reads, or null when there are none.</summary>
    internal static string? PatternsArg(IEnumerable<string> patterns)
    {
        var list = patterns.Distinct(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : new System.Text.Json.Nodes.JsonArray(list.Select(p => (System.Text.Json.Nodes.JsonNode?)p).ToArray()).ToJsonString();
    }

    /// <summary>The single-player run's own world, made once at the start and loaded fresh for each suite that needs it.</summary>
    public const string RunWorld = "mltest-run";

    /// <param name="mods">The profile's enabled mods: id → installed version (null when unknown).</param>
    /// <param name="onlyMod">With <c>--mod X</c>: only suites that require X, unless <paramref name="withOthers"/>.</param>
    /// <param name="modules">With <c>--modules</c>: only these test modules' suites (decision 29); null for every module.</param>
    /// <param name="notes">Told why each suite left out was left out.</param>
    public static IReadOnlyList<TestPlan> Select(IEnumerable<TestPlan> available, IReadOnlyDictionary<string, string?> mods,
        bool singlePlayer, string? onlyMod, bool withOthers, List<string> notes, IReadOnlyCollection<string>? modules = null)
    {
        var all = available.ToList();
        var core = all.FirstOrDefault(p => p.Name == CoreSuite) ?? throw new InvalidOperationException("the core suite is missing");
        var picked = new List<TestPlan>();
        foreach (var plan in all.Where(p => p.Name != CoreSuite).OrderBy(p => p.Module, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var label = plan.Module.Length > 0 ? $"{plan.Module}/{plan.Name}" : plan.Name;
            if (modules is not null && !modules.Contains(plan.Module, StringComparer.OrdinalIgnoreCase)) continue;
            // Unpicked, a v1 suite declares neither mode nor requirements, so nothing says where it applies. A module
            // picked by name runs its v1 suites too, in the mode their sides say.
            if (plan.Api < 2 && modules is null) continue;
            if (!plan.RunsIn(singlePlayer)) { notes.Add($"{label}: {(singlePlayer ? "Coop" : "single-player")} only"); continue; }
            var unmet = Unmet(plan.Requires, mods);
            if (unmet.Count > 0) { notes.Add($"{label}: needs {string.Join(", ", unmet.Select(r => Describe(r, mods)))}"); continue; }
            if (onlyMod is not null && !withOthers && !plan.Requires.Any(r => r.Id.Equals(onlyMod, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"{label}: does not require {onlyMod} (--with-others runs it too)");
                continue;
            }
            picked.Add(plan);
        }
        // The suites that share a world ride on core's: core changes little, and in Coop they then cost no launch of
        // their own. Each suite that needs a fresh world follows, and only it sees the world it leaves.
        return [core.ForMode(singlePlayer), .. picked.Where(p => p.SharesWorld).Select(p => p.ForMode(singlePlayer)),
            .. picked.Where(p => !p.SharesWorld).Select(p => p.ForMode(singlePlayer))];
    }

    /// <summary>The requirements <paramref name="mods"/> do not meet.</summary>
    public static IReadOnlyList<ModRequirement> Unmet(IEnumerable<ModRequirement> requires, IReadOnlyDictionary<string, string?> mods) =>
        requires.Where(r => !mods.TryGetValue(r.Id, out var version) || !r.MetBy(version)).ToList();

    public static string Describe(ModRequirement r, IReadOnlyDictionary<string, string?> mods) =>
        mods.TryGetValue(r.Id, out var version) ? $"{r} (this profile has {version ?? "an unknown version"})" : $"{r} (not in this profile)";

    /// <summary>
    /// The Coop launches for <paramref name="suites"/> (in order): a suite that needs a fresh world starts a new
    /// launch, and a suite that shares one joins the launch before it. Restarting the server inside one launch comes
    /// later; until then a fresh world costs a launch.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TestPlan>> CoopLaunches(IReadOnlyList<TestPlan> suites)
    {
        var launches = new List<List<TestPlan>>();
        foreach (var suite in suites)
        {
            if (launches.Count == 0 || !suite.SharesWorld) launches.Add([]);
            launches[^1].Add(suite);
        }
        return launches;
    }

    /// <summary>Single player: make the run's world before the first suite, load it fresh before each later one.</summary>
    public static IReadOnlyList<PlanStep> SinglePlayerSetup(TestPlan suite, int index, string? world)
    {
        const string side = TestPlan.GameSide;
        if (index == 0)
            return world is null
                ? [new PlanStep { Id = "game.createWorld", Side = side, As = "world", Timeout = TimeSpan.FromMinutes(15), Args = new Dictionary<string, string> { ["name"] = RunWorld, ["overwrite"] = "true" } }]
                : [new PlanStep { Id = "game.loadSave", Side = side, As = "world", Timeout = TimeSpan.FromMinutes(15), Args = new Dictionary<string, string> { ["name"] = world } }];
        if (suite.SharesWorld) return [];
        return
        [
            new PlanStep { Id = "game.toMainMenu", Side = side, As = "leave", Timeout = TimeSpan.FromMinutes(5) },
            new PlanStep { Id = "game.loadSave", Side = side, As = "world", Timeout = TimeSpan.FromMinutes(15), Args = new Dictionary<string, string> { ["name"] = world ?? RunWorld } },
            new PlanStep { Id = "game.pause", Side = side, As = "pause" },
        ];
    }

    /// <summary>Coop: before the first suite of a launch, wait for the join and stop campaign time, as v1 suites do.</summary>
    public static IReadOnlyList<PlanStep> CoopSetup(TestPlan suite, int index) => index > 0 ? [] :
    [
        new PlanStep { Id = "game.onMap", Side = "server", As = "server.onMap" },
        new PlanStep { Id = "game.onMap", Side = "client:1", As = "client.onMap" },
        new PlanStep { Id = "game.joined", Side = "client:1", As = "joined" },
        new PlanStep { Id = "game.pause", Side = "server", As = "pause" },
    ];

    /// <summary>The closing check's suite name: on-screen errors after the core suite's check, on every side.</summary>
    public const string EndSuite = "end";

    /// <summary>
    /// One plan made of <paramref name="suites"/>, each preceded by its <paramref name="setup"/> steps. Step names
    /// and their "$name.key" references are prefixed with the suite's name, so two suites can name steps alike.
    /// Every <c>game.noErrors</c> also fails on every suite's <c>errorPatterns</c>, and the plan ends with one more on
    /// each side for errors shown since (it runs even when a suite failed, or its world never loaded).
    /// </summary>
    public static TestPlan Compose(string name, IReadOnlyList<TestPlan> suites, Func<TestPlan, int, IReadOnlyList<PlanStep>> setup)
    {
        var patterns = PatternsArg(suites.SelectMany(s => s.ErrorPatterns));
        var steps = new List<PlanStep>();
        for (int i = 0; i < suites.Count; i++)
        {
            var suite = suites[i];
            string Prefix(string stepName) => suite.Name + "/" + stepName;
            foreach (var step in setup(suite, i).Concat(suite.Steps))
            {
                var args = step.Args.ToDictionary(kv => kv.Key,
                    kv => TestPlan.Reference(kv.Value) is { } r ? $"${Prefix(r.Step)}.{r.Key}" : kv.Value, StringComparer.Ordinal);
                if (step.Id == NoErrorsStep && patterns is not null) args["patterns"] = patterns;
                steps.Add(step with { As = Prefix(step.Name), Args = args, Suite = suite.Name });
            }
        }
        var sides = steps.SelectMany(s => s.IsCompare ? s.CompareSides : [s.Side]).Distinct().OrderBy(s => s == "server" ? "" : s, StringComparer.Ordinal).ToList();
        foreach (var side in sides)
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["new"] = "true" };
            if (patterns is not null) args["patterns"] = patterns;
            steps.Add(new PlanStep { Id = NoErrorsStep, Side = side, As = $"{EndSuite}/noErrors.{side}", Args = args, ContinueOnFail = true, Suite = EndSuite });
        }
        // The first suite gets the world its setup gives; each later one shares it unless it asked for a fresh one.
        var worlds = new List<List<string>>();
        for (int i = 0; i < suites.Count; i++)
        {
            if (i == 0 || !suites[i].SharesWorld) worlds.Add([]);
            worlds[^1].Add(suites[i].Name);
        }
        return new TestPlan { Name = name, Api = TestPlan.SupportedApi, Steps = steps, Mode = suites.FirstOrDefault()?.Mode ?? SuiteMode.Coop, Worlds = worlds };
    }
}
