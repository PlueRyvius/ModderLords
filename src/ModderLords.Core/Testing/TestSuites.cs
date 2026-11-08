using System.Text.Json;
using System.Text.Json.Nodes;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Testing;

/// <summary>
/// A test module (docs/INGAME-TEST-HARNESS.md, decisions 28–31): a folder of suites for one area, shipped with
/// ModderLords. A run loads only the modules picked, plus <c>core</c>.
/// </summary>
public sealed record TestModule
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Mods every suite in the module needs; added to each suite's own.</summary>
    public IReadOnlyList<ModRequirement> Requires { get; init; } = [];
    /// <summary>The module's own step DLL (in ModderLords' compat folder), for steps that call a mod's code; null for none.</summary>
    public string? StepsDll { get; init; }
    public required IReadOnlyList<TestPlan> Suites { get; init; }

    /// <summary>Parses <c>module.json</c>; throws <see cref="FormatException"/> naming the problem.</summary>
    public static TestModule Parse(string json, IReadOnlyList<TestPlan> suites)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new FormatException("module.json is a JSON object"); }
        catch (JsonException ex) { throw new FormatException("module.json is not valid JSON: " + ex.Message); }
        var id = root["id"]?.GetValue<string>() ?? throw new FormatException("module.json has no id");
        var requires = new List<ModRequirement>();
        if (root["requires"] is JsonArray reqNode)
            foreach (var r in reqNode)
                requires.Add(r switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => new ModRequirement(s),
                    JsonObject o => new ModRequirement(o["id"]?.GetValue<string>() ?? throw new FormatException($"module {id}: a requirement has no \"id\""), o["version"]?.GetValue<string>()),
                    _ => throw new FormatException($"module {id}: \"requires\" lists module ids or {{ \"id\", \"version\" }} objects"),
                });
        foreach (var r in requires.Where(r => r.Version is not null))
            if (!VersionRange.IsValid(r.Version!)) throw new FormatException($"module {id}: requirement {r.Id}: \"{r.Version}\" is not a version or range");
        var stepsDll = root["steps"]?.GetValue<string>();
        if (stepsDll is not null && !stepsDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"module {id}: \"steps\" names a DLL, not \"{stepsDll}\"");
        return new TestModule
        {
            Id = id,
            Title = root["title"]?.GetValue<string>() ?? id,
            Description = root["description"]?.GetValue<string>() ?? "",
            Requires = requires,
            StepsDll = stepsDll,
            // A suite carries its module, and needs what its module needs as well as its own.
            Suites = suites.Select(s => s with { Module = id, Requires = [.. requires, .. s.Requires.Where(r => !requires.Any(m => m.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)))] }).ToList(),
        };
    }

    /// <summary>The type a module DLL's steps register from: its file name with ".Steps" (ModderLords.Tests.Smithing.Steps).</summary>
    public string? StepsType => StepsDll is null ? null : Path.GetFileNameWithoutExtension(StepsDll) + ".Steps";
}

/// <summary>The test modules shipped inside the launcher (Testing/Modules/&lt;module&gt;/), and where test worlds live.</summary>
public static class TestSuites
{
    private const string Prefix = "ModderLords.Core.TestModules.";

    private static readonly Lazy<IReadOnlyList<TestModule>> Built = new(LoadModules);

    /// <summary>Every built-in module, in id order, <c>core</c> first.</summary>
    public static IReadOnlyList<TestModule> Modules() => Built.Value;

    private static IReadOnlyList<TestModule> LoadModules()
    {
        var assembly = typeof(TestSuites).Assembly;
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => (Resource: n, Path: n[Prefix.Length..].Replace('\\', '/')))
            .Where(f => f.Path.Contains('/'))
            .GroupBy(f => f.Path[..f.Path.IndexOf('/')], StringComparer.Ordinal);
        var modules = new List<TestModule>();
        foreach (var folder in files)
        {
            string Read(string resource)
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            var manifest = folder.FirstOrDefault(f => f.Path.EndsWith("/module.json", StringComparison.Ordinal));
            if (manifest.Resource is null) throw new FormatException($"test module {folder.Key} has no module.json");
            var suites = folder.Where(f => f != manifest).OrderBy(f => f.Path, StringComparer.Ordinal).Select(f =>
            {
                try { return TestPlan.Parse(Read(f.Resource)); }
                catch (FormatException ex) { throw new FormatException($"{f.Path}: {ex.Message}"); }
            }).ToList();
            var module = TestModule.Parse(Read(manifest.Resource), suites);
            if (module.Id != folder.Key) throw new FormatException($"test module folder {folder.Key} says its id is {module.Id}");
            modules.Add(module);
        }
        return modules.OrderBy(m => m.Id == SuiteSelection.CoreSuite ? "" : m.Id, StringComparer.Ordinal).ToList();
    }

    public static TestModule? Module(string id) => Modules().FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every suite as <c>module/suite</c>.</summary>
    public static IReadOnlyList<string> Names() => Modules().SelectMany(m => m.Suites.Select(s => $"{m.Id}/{s.Name}")).ToList();

    /// <summary>A built-in suite as <c>module/suite</c>, or by its own name when that is unique; or a suite file by path.</summary>
    public static TestPlan Load(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return TestPlan.Parse(File.ReadAllText(nameOrPath));
        var slash = nameOrPath.IndexOf('/');
        var matches = slash > 0
            ? Modules().Where(m => m.Id == nameOrPath[..slash]).SelectMany(m => m.Suites).Where(s => s.Name == nameOrPath[(slash + 1)..]).ToList()
            : Modules().SelectMany(m => m.Suites).Where(s => s.Name == nameOrPath).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FormatException($"no suite '{nameOrPath}' (built in: {string.Join(", ", Names())}; or give a file path)"),
            _ => throw new FormatException($"'{nameOrPath}' is in several modules: {string.Join(", ", matches.Select(s => s.Module + "/" + s.Name))}"),
        };
    }

    /// <summary>Every built-in suite, for a run that picks its own (SuiteSelection).</summary>
    public static IReadOnlyList<TestPlan> LoadAll() => Modules().SelectMany(m => m.Suites).ToList();

    /// <summary>Test worlds: saves kept apart from the server's own, copied fresh into it for every run.</summary>
    public static string WorldsDir => Path.Combine(ProfileStore.RootDir, "test-worlds");

    public static string WorldPath(string name) => Path.Combine(WorldsDir, name + ".sav");
}
