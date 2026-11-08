using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModderLords.Core.Testing;

/// <summary>Where a suite (or one of its steps) runs: a single-player game, a Coop session, or either.</summary>
public enum SuiteMode { Both, SinglePlayer, Coop }

/// <summary>A mod a suite needs, optionally within a version range (">=1.2", "<2", "=1.3.0", or a bare version).</summary>
public sealed record ModRequirement(string Id, string? Version = null)
{
    public bool MetBy(string? installedVersion) => Version is null || (installedVersion is not null && VersionRange.Matches(installedVersion, Version));

    public override string ToString() => Version is null ? Id : $"{Id} {Version}";
}

/// <summary>
/// One entry of a suite: a step run on one side, or a compare (the same step run on several sides, whose reported
/// <c>value</c>s must agree). Format: docs/INGAME-TEST-HARNESS.md.
/// </summary>
public sealed record PlanStep
{
    /// <summary>The registered step to run (for a compare, the step each side runs).</summary>
    public required string Id { get; init; }
    /// <summary>"server" or "client:N" in Coop, "game" in single player; empty for a compare.</summary>
    public string Side { get; init; } = "";
    /// <summary>For a compare: the sides whose values must agree.</summary>
    public IReadOnlyList<string> CompareSides { get; init; } = [];
    public bool IsCompare => CompareSides.Count > 0;
    /// <summary>Name later steps use to read this one's values ("$name.key"); defaults to <see cref="Id"/>.</summary>
    public string? As { get; init; }
    public string Name => As ?? Id;
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool ContinueOnFail { get; init; }
    /// <summary>Set when the step is expected to fail until an upstream fix: the issue's link.</summary>
    public string? KnownFailing { get; init; }
    /// <summary>In a suite that runs both ways, a step that only makes sense in one of them (a join, a compare).</summary>
    public SuiteMode Mode { get; init; } = SuiteMode.Both;
    /// <summary>The suite this step came from, in a run made of several suites; empty in a run of one.</summary>
    public string Suite { get; init; } = "";

    public string Describe() => IsCompare ? $"compare {Id} on {string.Join(" / ", CompareSides)}" : $"{Id} on {Side}";
}

public sealed record TestPlan
{
    /// <summary>The newest suite format this launcher understands.</summary>
    public const int SupportedApi = 2;
    /// <summary>The in-game step API a run asks the games for (run.json); versioned apart from the suite format.</summary>
    public const int InGameApi = 1;

    /// <summary>The one side of a single-player run: the game is both the world and the player.</summary>
    public const string GameSide = "game";
    /// <summary>In a suite that runs both ways: the side that acts as the player (Coop: client:1; single player: the game).</summary>
    public const string PlayerSide = "player";

    /// <summary>The world value that runs a suite on the world the previous suite left (decision 19).</summary>
    public const string SharedWorld = "shared";

    public required string Name { get; init; }
    public int Api { get; init; } = 1;
    public string Description { get; init; } = "";
    /// <summary>The world the run starts from, copied fresh (a name under the test-worlds folder); null = the profile's save.</summary>
    public string? World { get; init; }
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public SuiteMode Mode { get; init; } = SuiteMode.Coop;
    public IReadOnlyList<ModRequirement> Requires { get; init; } = [];
    /// <summary>The suite saves and reloads on purpose and checks what survived (decision 16).</summary>
    public bool Continuity { get; init; }
    /// <summary>The suite opted in to running on the previous suite's world (decision 19).</summary>
    public bool SharesWorld => World == SharedWorld;
    /// <summary>Patterns (regular expressions) <c>game.noErrors</c> also fails on, beyond its default set (decision 25).</summary>
    public IReadOnlyList<string> ErrorPatterns { get; init; } = [];

    /// <summary>Every side a step runs on, server first.</summary>
    public IReadOnlyList<string> Sides =>
        Steps.SelectMany(s => s.IsCompare ? s.CompareSides : [s.Side]).Distinct().OrderBy(s => s == "server" ? "" : s, StringComparer.Ordinal).ToList();

    /// <summary>The highest client slot used (1 for "client:1").</summary>
    public int Clients => Sides.Where(s => s.StartsWith("client:", StringComparison.Ordinal)).Select(s => int.Parse(s[7..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();

    /// <summary>A single-player plan: every step runs on "game", and no server is started.</summary>
    public bool SinglePlayer => Sides.Count == 1 && Sides[0] == GameSide;

    public static bool IsSide(string side) =>
        side == "server" || side == GameSide ||
        (side.StartsWith("client:", StringComparison.Ordinal) && int.TryParse(side[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0);

    /// <summary>
    /// The plan as it runs in one mode. A suite that runs both ways names <c>game</c> (the side that changes the world)
    /// and <c>player</c>: in single player both are the game; in Coop they are the server and client:1. Steps marked for
    /// the other mode are left out, and so are compares in single player, where there is one side.
    /// </summary>
    public TestPlan ForMode(bool singlePlayer)
    {
        if (Mode != SuiteMode.Both) return this;
        string Map(string side) => side switch
        {
            GameSide => singlePlayer ? GameSide : "server",
            PlayerSide => singlePlayer ? GameSide : "client:1",
            _ => side,
        };
        var wanted = singlePlayer ? SuiteMode.SinglePlayer : SuiteMode.Coop;
        var steps = Steps.Where(s => s.Mode == SuiteMode.Both || s.Mode == wanted)
            .Where(s => !(singlePlayer && s.IsCompare))
            .Select(s => s with { Side = s.IsCompare ? "" : Map(s.Side), CompareSides = s.CompareSides.Select(Map).ToList() })
            .ToList();
        return this with { Steps = steps, Mode = wanted };
    }

    public bool RunsIn(bool singlePlayer) => Mode == SuiteMode.Both || Mode == (singlePlayer ? SuiteMode.SinglePlayer : SuiteMode.Coop);

    /// <summary>Parses and validates a suite; throws <see cref="FormatException"/> naming the first problem.</summary>
    public static TestPlan Parse(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new FormatException("a suite is a JSON object"); }
        catch (JsonException ex) { throw new FormatException("not valid JSON: " + ex.Message); }

        var api = root["api"]?.GetValue<int>() ?? 1;
        if (api < 1 || api > SupportedApi) throw new FormatException($"the suite needs test API v{api}; this launcher understands up to v{SupportedApi}");
        var name = root["name"]?.GetValue<string>() ?? throw new FormatException("the suite has no name");
        var stepsNode = root["steps"] as JsonArray ?? throw new FormatException("the suite has no steps");
        if (api == 1 && (root["mode"] is not null || root["requires"] is not null || root["continuity"] is not null || root["errorPatterns"] is not null))
            throw new FormatException("\"mode\", \"requires\", \"continuity\" and \"errorPatterns\" need \"api\": 2");
        var suiteMode = root["mode"] is { } modeNode ? ParseMode(modeNode.GetValue<string>(), "the suite") : (SuiteMode?)null;

        var steps = new List<PlanStep>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < stepsNode.Count; i++)
        {
            var where = $"step {i + 1}";
            var node = stepsNode[i] as JsonObject ?? throw new FormatException(where + " is not an object");
            var compare = node["compare"]?.GetValue<string>();
            var id = compare ?? node["id"]?.GetValue<string>() ?? throw new FormatException(where + " has neither \"id\" nor \"compare\"");
            where += $" ({id})";
            var side = node["side"]?.GetValue<string>() ?? "";
            var sides = (node["sides"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? [];
            // "player" is a side only in a suite that runs both ways, where it is resolved per run.
            bool Valid(string s) => IsSide(s) || (suiteMode == SuiteMode.Both && s == PlayerSide);
            if (compare is null)
            {
                if (!Valid(side)) throw new FormatException($"{where}: \"side\" must be \"server\", \"client:N\" or \"game\"{(suiteMode == SuiteMode.Both ? " or \"player\"" : "")}, not \"{side}\"");
                if (sides.Count > 0) throw new FormatException($"{where}: \"sides\" belongs to a compare");
            }
            else
            {
                if (side.Length > 0) throw new FormatException($"{where}: a compare takes \"sides\", not \"side\"");
                if (sides.Count < 2 || sides.Distinct().Count() != sides.Count) throw new FormatException($"{where}: a compare needs two or more different sides");
                if (sides.FirstOrDefault(s => !Valid(s)) is { } bad) throw new FormatException($"{where}: unknown side \"{bad}\"");
            }
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            if (node["args"] is JsonObject argsNode)
                foreach (var (key, value) in argsNode)
                    args[key] = value switch
                    {
                        null => "",
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        _ => value.ToJsonString(),
                    };
            var timeout = node["timeoutSeconds"]?.GetValue<double>() ?? 60;
            if (timeout <= 0) throw new FormatException($"{where}: timeoutSeconds must be positive");
            var stepMode = node["mode"] is { } sm ? ParseMode(sm.GetValue<string>(), where) : SuiteMode.Both;
            if (stepMode != SuiteMode.Both && suiteMode != SuiteMode.Both) throw new FormatException($"{where}: a step's \"mode\" belongs to a suite with \"mode\": \"both\"");
            var step = new PlanStep
            {
                Id = id,
                Side = side,
                CompareSides = compare is null ? [] : sides,
                As = node["as"]?.GetValue<string>(),
                Args = args,
                Timeout = TimeSpan.FromSeconds(timeout),
                ContinueOnFail = node["continueOnFail"]?.GetValue<bool>() ?? false,
                KnownFailing = node["knownFailing"]?.GetValue<string>(),
                Mode = stepMode,
            };
            // A reference must name a step that ran earlier; a typo would otherwise surface as a puzzling in-game failure.
            foreach (var (key, value) in args)
                if (Reference(value) is { } r && !names.Contains(r.Step))
                    throw new FormatException($"{where}: argument {key} refers to \"{r.Step}\", which is not an earlier step (name a step with \"as\" to refer to it)");
            names.Add(step.Name);
            steps.Add(step);
        }
        if (steps.Count == 0) throw new FormatException("the suite has no steps");

        // A single-player game has no server or other players to address; a Coop run has no "game".
        var allSides = steps.SelectMany(s => s.IsCompare ? s.CompareSides : [s.Side]).Distinct().ToList();
        var coopSides = allSides.Where(s => s != GameSide && s != PlayerSide).ToList();
        var mode = suiteMode ?? (allSides.All(s => s == GameSide) ? SuiteMode.SinglePlayer : SuiteMode.Coop);
        if (mode == SuiteMode.SinglePlayer && coopSides.Count > 0)
            throw new FormatException($"a single-player suite runs on \"{GameSide}\" only, not {string.Join(", ", coopSides)}");
        if (mode == SuiteMode.Coop && allSides.Contains(GameSide))
            throw new FormatException(coopSides.Count > 0
                ? $"the suite mixes \"{GameSide}\" (single player) with {string.Join(", ", coopSides)} (Coop); a suite is one or the other"
                : $"a Coop suite names \"server\" and \"client:N\", not \"{GameSide}\"");
        if (mode == SuiteMode.Both && coopSides.Count > 0)
            throw new FormatException($"a suite that runs both ways names \"{GameSide}\" and \"{PlayerSide}\", not {string.Join(", ", coopSides)}");

        var requires = new List<ModRequirement>();
        if (root["requires"] is JsonArray reqNode)
            foreach (var r in reqNode)
                requires.Add(r switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => new ModRequirement(s),
                    JsonObject o => new ModRequirement(o["id"]?.GetValue<string>() ?? throw new FormatException("a requirement has no \"id\""), o["version"]?.GetValue<string>()),
                    _ => throw new FormatException("\"requires\" lists module ids or { \"id\", \"version\" } objects"),
                });
        var errorPatterns = (root["errorPatterns"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(p => p.Length > 0).ToList() ?? [];
        foreach (var pattern in errorPatterns)
        {
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException ex) { throw new FormatException($"errorPatterns: \"{pattern}\" is not a regular expression: {ex.Message}"); }
        }
        foreach (var r in requires.Where(r => r.Version is not null))
            if (!VersionRange.IsValid(r.Version!)) throw new FormatException($"requirement {r.Id}: \"{r.Version}\" is not a version or range (>=1.2, <2, =1.3.0)");

        return new TestPlan
        {
            Name = name,
            Api = api,
            Description = root["description"]?.GetValue<string>() ?? "",
            World = root["world"]?.GetValue<string>(),
            Steps = steps,
            Mode = mode,
            Requires = requires,
            Continuity = root["continuity"]?.GetValue<bool>() ?? false,
            ErrorPatterns = errorPatterns,
        };
    }

    private static SuiteMode ParseMode(string value, string where) => value switch
    {
        "both" => SuiteMode.Both,
        "singleplayer" => SuiteMode.SinglePlayer,
        "coop" => SuiteMode.Coop,
        _ => throw new FormatException($"{where}: \"mode\" is singleplayer, coop or both, not \"{value}\""),
    };

    /// <summary>"$step.key" → (step, key); a step name may itself contain dots, so the key is after the last one.</summary>
    public static (string Step, string Key)? Reference(string value)
    {
        if (value.Length < 4 || value[0] != '$') return null;
        var dot = value.LastIndexOf('.');
        return dot <= 1 || dot == value.Length - 1 ? null : (value[1..dot], value[(dot + 1)..]);
    }
}

/// <summary>Module versions as SubModule.xml writes them ("v1.2.3"), compared number by number.</summary>
public static class VersionRange
{
    private static readonly string[] Operators = [">=", "<=", ">", "<", "="];

    public static bool IsValid(string range) => Parse(Split(range).Version) is not null;

    public static bool Matches(string installed, string range)
    {
        var (op, version) = Split(range);
        var have = Parse(installed);
        var want = Parse(version);
        if (have is null || want is null) return false;
        var c = Compare(have, want);
        return op switch { ">=" => c >= 0, "<=" => c <= 0, ">" => c > 0, "<" => c < 0, _ => c == 0 };
    }

    private static (string Op, string Version) Split(string range)
    {
        var r = range.Trim();
        var op = Operators.FirstOrDefault(o => r.StartsWith(o, StringComparison.Ordinal)) ?? "=";
        return (op, r.StartsWith(op, StringComparison.Ordinal) ? r[op.Length..].Trim() : r);
    }

    /// <summary>"v1.2.3" (also "e1.2.3", "b1.2.3") as numbers; null when it is not one.</summary>
    private static int[]? Parse(string version)
    {
        var v = version.Trim();
        if (v.Length > 0 && char.IsLetter(v[0])) v = v[1..];
        var parts = v.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers;
    }

    /// <summary>Missing parts count as 0, so 1.2 equals 1.2.0.</summary>
    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
