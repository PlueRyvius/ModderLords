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
    /// <param name="worlds">The test worlds kept under test-worlds, by name, for suites that run on upgrade worlds.</param>
    /// <param name="savedOn">The game version each kept world was saved on, for <see cref="UpgradeWorlds"/>.</param>
    /// <param name="installedGame">The installed game's version; upgrade worlds from a newer game are left out.</param>
    public static IReadOnlyList<TestPlan> Select(IEnumerable<TestPlan> available, IReadOnlyDictionary<string, string?> mods,
        bool singlePlayer, string? onlyMod, bool withOthers, List<string> notes, IReadOnlyCollection<string>? modules = null,
        IReadOnlyCollection<string>? worlds = null, Func<string, string?>? savedOn = null, string? installedGame = null)
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
            if (plan.Manual) { notes.Add($"{label}: runs only when named (--suite {label})"); continue; }
            if (!plan.RunsIn(singlePlayer)) { notes.Add($"{label}: {(singlePlayer ? "Coop" : "single-player")} only"); continue; }
            var unmet = Unmet(plan.Requires, mods);
            if (unmet.Count > 0) { notes.Add($"{label}: needs {string.Join(", ", unmet.Select(r => Describe(r, mods)))}"); continue; }
            if (onlyMod is not null && !withOthers && !plan.Requires.Any(r => r.Id.Equals(onlyMod, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"{label}: does not require {onlyMod} (--with-others runs it too)");
                continue;
            }
            if (plan.OnUpgradeWorlds)
            {
                var before = notes.Count;
                var kept = UpgradeWorlds(plan, worlds ?? [], savedOn, installedGame, notes);
                if (kept.Count == 0 && notes.Count == before)
                {
                    notes.Add($"{label}: no upgrade world kept for {string.Join(" or ", plan.Requires.Select(r => r.Id))} " +
                              $"(test-worlds\\{TestPlan.UpgradePrefix}<ModId>-<version>.sav, made by a suite that keeps one)");
                    continue;
                }
                picked.AddRange(kept);   // none when every kept world is from a newer game; each said so
                continue;
            }
            picked.Add(plan);
        }
        // A suite that continues another's kept world runs only with that suite, in the launch right after its.
        var continuing = picked.Where(p => p.After is not null).ToList();
        foreach (var plan in continuing)
        {
            picked.Remove(plan);
            if (!picked.Any(p => p.Module == plan.Module && p.Name == plan.After))
                notes.Add($"{(plan.Module.Length > 0 ? plan.Module + "/" : "")}{plan.Name}: runs after {plan.After}, which this run does not include");
        }
        // The suites that share a world ride on core's: core changes little, and in Coop they then cost no launch of
        // their own. Each suite that needs a fresh world follows, and only it sees the world it leaves.
        var fresh = new List<TestPlan>();
        foreach (var plan in picked.Where(p => !p.SharesWorld))
        {
            fresh.Add(plan);
            // Each continuation follows its suite, and a continuation of a continuation follows that one.
            for (int k = fresh.Count - 1; k < fresh.Count; k++)
                fresh.AddRange(continuing.Where(c => c.Module == fresh[k].Module && c.After == fresh[k].Name));
        }
        return [core.ForMode(singlePlayer), .. picked.Where(p => p.SharesWorld).Select(p => p.ForMode(singlePlayer)),
            .. fresh.Select(p => p.ForMode(singlePlayer))];
    }

    /// <summary>
    /// One copy of an upgrade suite per kept upgrade world of a mod it requires (decision 27), oldest version first,
    /// each named <c>suite@version</c> and loading that world. A world saved on a newer game than the installed one
    /// (<paramref name="savedOn"/> against <paramref name="installedGame"/>) is left out, with a line in
    /// <paramref name="notes"/>: an older game crashes loading a newer game's save.
    /// </summary>
    /// <param name="savedOn">The game version a kept world was saved on ("v1.5.7.125802"); null when unknown.</param>
    /// <param name="installedGame">The installed game's version ("v1.4.8"); null when unknown, and then every world runs.</param>
    public static IReadOnlyList<TestPlan> UpgradeWorlds(TestPlan suite, IEnumerable<string> worlds,
        Func<string, string?>? savedOn = null, string? installedGame = null, List<string>? notes = null)
    {
        var label = suite.Module.Length > 0 ? $"{suite.Module}/{suite.Name}" : suite.Name;
        var copies = new List<(string Version, TestPlan Plan)>();
        var newer = new List<(string Version, string Line)>();
        foreach (var world in worlds)
            foreach (var mod in suite.Requires.Select(r => r.Id))
            {
                var prefix = TestPlan.UpgradePrefix + mod + "-";
                if (!world.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || world.Length == prefix.Length) continue;
                var version = world[prefix.Length..];
                if (savedOn?.Invoke(world) is { } saved && NewerGame(saved, installedGame))
                {
                    newer.Add((version, $"{label}@{version}: its world was saved on game {ShortGameVersion(saved)}, " +
                                        $"newer than the installed {ShortGameVersion(installedGame!)} (an older game crashes loading it)"));
                    continue;
                }
                copies.Add((version, suite with { Name = $"{suite.Name}@{version}", World = world }));
            }
        notes?.AddRange(newer.OrderBy(n => n.Version, VersionOrder.Instance).Select(n => n.Line));
        return copies.OrderBy(c => c.Version, VersionOrder.Instance).Select(c => c.Plan).ToList();
    }

    /// <summary>
    /// Whether a save made on game <paramref name="saved"/> is from a newer game than <paramref name="installed"/>.
    /// Compares major.minor.revision only: a save's "v1.4.8.118999" is the same game as Version.xml's "v1.4.8". False
    /// when either is unknown or not a version, so a world runs unless it is known to be newer.
    /// </summary>
    public static bool NewerGame(string? saved, string? installed) =>
        GameVersionParts(saved) is { } s && GameVersionParts(installed) is { } i
        && s.Zip(i).Select(p => p.First.CompareTo(p.Second)).FirstOrDefault(c => c != 0) > 0;

    /// <summary>The game version as major.minor.revision with its letter ("v1.5.7.125802" → "v1.5.7").</summary>
    public static string ShortGameVersion(string version)
    {
        var parts = GameVersionParts(version);
        if (parts is null) return version;
        var letter = version.TrimStart()[0] is var c && char.IsLetter(c) ? c.ToString() : "";
        return letter + string.Join(".", parts);
    }

    private static int[]? GameVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim().TrimStart('v', 'e', 'b', 'a', 'd', 'V');
        var parts = text.Split('.');
        if (parts.Length < 3) return null;
        var numbers = new int[3];
        for (int k = 0; k < 3; k++)
            if (!int.TryParse(parts[k], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[k])) return null;
        return numbers;
    }

    /// <summary>Versions in number order ("v0.9.49" before "v0.9.100"); text that is not a version sorts by text.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            if (VersionRange.IsValid(x) && VersionRange.IsValid(y))
                return VersionRange.Matches(x, "<" + y) ? -1 : VersionRange.Matches(x, "=" + y) ? 0 : 1;
            return string.CompareOrdinal(x, y);
        }
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

    /// <summary>The save a test world is copied to in the game's saves, fresh for each run.</summary>
    public static string SaveFor(string testWorld) => "mltest-" + testWorld;

    /// <summary>
    /// Single player: make the run's world before the first suite, load it fresh before each later one. A suite with a
    /// world of its own (a named test world, or one upgrade world of an upgrade suite) loads that one instead.
    /// </summary>
    public static IReadOnlyList<PlanStep> SinglePlayerSetup(TestPlan suite, int index, string? world)
    {
        const string side = TestPlan.GameSide;
        var own = suite.NamedWorld is { } named ? SaveFor(named) : null;
        PlanStep Load(string name) => new() { Id = "game.loadSave", Side = side, As = "world", Timeout = TimeSpan.FromMinutes(15), Args = new Dictionary<string, string> { ["name"] = name } };
        if (index == 0)
            return (own ?? world) is { } first
                ? [Load(first)]
                : [new PlanStep { Id = "game.createWorld", Side = side, As = "world", Timeout = TimeSpan.FromMinutes(15), Args = new Dictionary<string, string> { ["name"] = RunWorld, ["overwrite"] = "true" } }];
        if (suite.SharesWorld) return [];
        return
        [
            new PlanStep { Id = "game.toMainMenu", Side = side, As = "leave", Timeout = TimeSpan.FromMinutes(5) },
            Load(own ?? world ?? RunWorld),
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
                    kv => TestPlan.Reference(kv.Value) is { } r && !TestPlan.IsOtherSuite(r.Step) ? $"${Prefix(r.Step)}.{r.Key}" : kv.Value, StringComparer.Ordinal);
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
