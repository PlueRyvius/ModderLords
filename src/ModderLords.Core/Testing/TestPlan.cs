using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModderLords.Core.Testing;

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

    public string Describe() => IsCompare ? $"compare {Id} on {string.Join(" / ", CompareSides)}" : $"{Id} on {Side}";
}

public sealed record TestPlan
{
    /// <summary>The newest plan format this launcher understands, and the in-game API it asks for.</summary>
    public const int SupportedApi = 1;

    public required string Name { get; init; }
    public int Api { get; init; } = 1;
    public string Description { get; init; } = "";
    /// <summary>The world the run starts from, copied fresh (a name under the test-worlds folder); null = the profile's save.</summary>
    public string? World { get; init; }
    public required IReadOnlyList<PlanStep> Steps { get; init; }

    /// <summary>Every side a step runs on, server first.</summary>
    public IReadOnlyList<string> Sides =>
        Steps.SelectMany(s => s.IsCompare ? s.CompareSides : [s.Side]).Distinct().OrderBy(s => s == "server" ? "" : s, StringComparer.Ordinal).ToList();

    /// <summary>The highest client slot used (1 for "client:1").</summary>
    public int Clients => Sides.Where(s => s.StartsWith("client:", StringComparison.Ordinal)).Select(s => int.Parse(s[7..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();

    /// <summary>A single-player suite: every step runs on "game", and no server is started.</summary>
    public bool SinglePlayer => Sides.Count == 1 && Sides[0] == GameSide;

    /// <summary>The one side of a single-player run: the game is both the world and the player.</summary>
    public const string GameSide = "game";

    public static bool IsSide(string side) =>
        side == "server" || side == GameSide ||
        (side.StartsWith("client:", StringComparison.Ordinal) && int.TryParse(side[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0);

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
            if (compare is null)
            {
                if (!IsSide(side)) throw new FormatException($"{where}: \"side\" must be \"server\", \"client:N\" or \"game\", not \"{side}\"");
                if (sides.Count > 0) throw new FormatException($"{where}: \"sides\" belongs to a compare");
            }
            else
            {
                if (side.Length > 0) throw new FormatException($"{where}: a compare takes \"sides\", not \"side\"");
                if (sides.Count < 2 || sides.Distinct().Count() != sides.Count) throw new FormatException($"{where}: a compare needs two or more different sides");
                if (sides.FirstOrDefault(s => !IsSide(s)) is { } bad) throw new FormatException($"{where}: unknown side \"{bad}\"");
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
        if (allSides.Contains(GameSide) && allSides.Count > 1)
            throw new FormatException($"the suite mixes \"{GameSide}\" (single player) with {string.Join(", ", allSides.Where(s => s != GameSide))} (Coop); a suite is one or the other");
        return new TestPlan
        {
            Name = name,
            Api = api,
            Description = root["description"]?.GetValue<string>() ?? "",
            World = root["world"]?.GetValue<string>(),
            Steps = steps,
        };
    }

    /// <summary>"$step.key" → (step, key); a step name may itself contain dots, so the key is after the last one.</summary>
    public static (string Step, string Key)? Reference(string value)
    {
        if (value.Length < 4 || value[0] != '$') return null;
        var dot = value.LastIndexOf('.');
        return dot <= 1 || dot == value.Length - 1 ? null : (value[1..dot], value[(dot + 1)..]);
    }
}
