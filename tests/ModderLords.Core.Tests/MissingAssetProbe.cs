using ModderLords.Core.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Answers "is the headless projection stripping the assets the server says it cannot find?" against a real
/// install, with evidence rather than inference.
///
/// Point it at an engine log full of "Could not find animation: X" warnings and it reports, for each distinct
/// name, which package supplies it — or MISSING EVERYWHERE. Measured 2026-09-11 on a TAOM server: 116 distinct
/// names, all of them missing from every installed package, client and server, so there was nothing for the
/// projection to include. They are dangling references in LOTRLOME_Armory's action_sets.xml.
///
/// Opt-in, because it needs a real game install:
///   MODDERLORDS_PROBE_LOG   an rgl_log_errors_*.txt (or any log with those warnings)
///   MODDERLORDS_PROBE_ROOTS module roots to search, separated by ';'
/// Skips quietly when they are unset, so it costs nothing in CI.
/// </summary>
public class MissingAssetProbe
{
    private readonly ITestOutputHelper _out;
    public MissingAssetProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Report_missing_assets_against_a_real_install()
    {
        var log = Environment.GetEnvironmentVariable("MODDERLORDS_PROBE_LOG");
        var roots = Environment.GetEnvironmentVariable("MODDERLORDS_PROBE_ROOTS");
        if (string.IsNullOrWhiteSpace(log) || string.IsNullOrWhiteSpace(roots))
        {
            _out.WriteLine("set MODDERLORDS_PROBE_LOG and MODDERLORDS_PROBE_ROOTS to run this; skipped");
            return;
        }
        if (!File.Exists(log)) { _out.WriteLine($"no log at {log}; skipped"); return; }

        var wanted = MissingNames(File.ReadLines(log)).ToList();
        _out.WriteLine($"{wanted.Count} distinct missing name(s) in {Path.GetFileName(log)}");

        var supplied = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entries = HeadlessAssetProjection.Inventory(root);
            _out.WriteLine($"  {root}: {entries.Count} record(s)");
            foreach (var e in entries)
            {
                if (!supplied.TryGetValue(e.Name, out var list)) supplied[e.Name] = list = new List<string>();
                list.Add($"{e.Type} in {Path.GetFileName(e.Package)}{(e.IsSimulation ? "" : " [NOT projected: render type]")}");
            }
        }

        var missing = 0;
        foreach (var name in wanted)
        {
            if (supplied.TryGetValue(name, out var where)) _out.WriteLine($"  {name}: {string.Join("; ", where.Distinct())}");
            else { missing++; _out.WriteLine($"  {name}: MISSING EVERYWHERE"); }
        }
        _out.WriteLine($"=== {missing} of {wanted.Count} exist in no installed package; " +
                       $"{wanted.Count - missing} exist and are worth checking against the projected types.");
    }

    /// <summary>Distinct names from the engine's asset-warning lines, in first-seen order.</summary>
    internal static IEnumerable<string> MissingNames(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            foreach (var marker in new[] { "Could not find animation:", "Combat parameter not found:" })
            {
                var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var name = line[(at + marker.Length)..].Trim().TrimEnd('.').Trim();
                if (name.Length > 0 && seen.Add(name)) yield return name;
            }
        }
    }

    [Fact]
    public void Parses_names_out_of_engine_warning_lines()
    {
        var lines = new[]
        {
            " WARNING: Could not find animation: female_run_right_1h. ",
            "[22:53:41.099]  WARNING: Could not find animation: female_run_right_1h.",   // duplicate
            " WARNING: Combat parameter not found: warg_attack_stand ",
            "nothing interesting here",
        };
        Assert.Equal(new[] { "female_run_right_1h", "warg_attack_stand" }, MissingNames(lines).ToArray());
    }

    [Fact]
    public void Inventory_of_a_missing_folder_is_empty_not_an_error()
        => Assert.Empty(HeadlessAssetProjection.Inventory(
            Path.Combine(Path.GetTempPath(), "mcprobe-no-such-" + Guid.NewGuid().ToString("N")[..8])));
}
