using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModderLords.Core.Testing;

/// <summary>
/// What <c>test --list</c> prints (decision 31): every test module, its suites and the steps they use, the mods each
/// needs, and, given a profile's mods, whether it can run there. JSON, so an AI agent finds what exists without
/// reading the source. Steps themselves are registered inside the game; a suite's list is the steps it names.
/// </summary>
public static class TestCatalog
{
    public static string ToJson(IReadOnlyList<TestModule> modules, IReadOnlyDictionary<string, string?>? mods, string? profile)
    {
        JsonArray Requires(IEnumerable<ModRequirement> rs) =>
            new(rs.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["version"] = r.Version }).ToArray());
        JsonNode? Available(IEnumerable<ModRequirement> rs)
        {
            if (mods is null) return null;
            var unmet = SuiteSelection.Unmet(rs, mods);
            return unmet.Count == 0 ? JsonValue.Create(true) : new JsonArray(unmet.Select(r => (JsonNode?)SuiteSelection.Describe(r, mods)).ToArray());
        }
        var root = new JsonObject
        {
            ["profile"] = profile,
            ["docs"] = "docs/WRITING-TEST-MODULES.md",
            ["modules"] = new JsonArray(modules.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id,
                ["title"] = m.Title,
                ["description"] = m.Description,
                ["requires"] = Requires(m.Requires),
                ["stepsDll"] = m.StepsDll,
                // true, or the unmet requirements; absent without --profile.
                ["available"] = Available(m.Requires),
                ["suites"] = new JsonArray(m.Suites.Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = $"{m.Id}/{s.Name}",
                    ["description"] = s.Description,
                    ["api"] = s.Api,
                    ["mode"] = s.Mode switch { SuiteMode.SinglePlayer => "singleplayer", SuiteMode.Coop => "coop", _ => "both" },
                    ["continuity"] = s.Continuity,
                    ["world"] = s.World,
                    ["requires"] = Requires(s.Requires),
                    ["available"] = Available(s.Requires),
                    ["steps"] = new JsonArray(s.Steps.Select(st => st.Id).Distinct().Select(id => (JsonNode?)id).ToArray()),
                }).ToArray()),
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
