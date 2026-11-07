using ModderLords.Core.Profiles;

namespace ModderLords.Core.Testing;

/// <summary>The suites shipped inside the launcher (Testing/Suites/*.json), and where test worlds live.</summary>
public static class TestSuites
{
    private const string Prefix = "ModderLords.Core.TestSuites.";

    public static IReadOnlyList<string> Names() =>
        typeof(TestSuites).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^".json".Length]).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>A built-in suite by name, or a suite file by path.</summary>
    public static TestPlan Load(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return TestPlan.Parse(File.ReadAllText(nameOrPath));
        using var stream = typeof(TestSuites).Assembly.GetManifestResourceStream(Prefix + nameOrPath + ".json")
            ?? throw new FormatException($"no suite '{nameOrPath}' (built in: {string.Join(", ", Names())}; or give a file path)");
        using var reader = new StreamReader(stream);
        return TestPlan.Parse(reader.ReadToEnd());
    }

    /// <summary>Test worlds: saves kept apart from the server's own, copied fresh into it for every run.</summary>
    public static string WorldsDir => Path.Combine(ProfileStore.RootDir, "test-worlds");

    public static string WorldPath(string name) => Path.Combine(WorldsDir, name + ".sav");
}
