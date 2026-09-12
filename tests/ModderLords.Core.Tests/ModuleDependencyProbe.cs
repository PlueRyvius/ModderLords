using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// What a module's assemblies actually reference, by name and version, read from IL metadata only.
///
/// <see cref="ModderLords.Core.Compat.AssemblyScan"/> answers "will this run headless?"; this answers the
/// different question "what does this need to exist in order to load at all?" — which is what decides whether a
/// third-party compat module can run against the stock dedicated server or only against a patched one.
///
/// Worked example (2026-09-12): TAOM's own TAOM.CoopCompat v0.3.17 ships alongside a wrapper containing a rebuilt
/// DedicatedServer.Core.dll. The question was whether the module needed that rebuild. It does not — it references
/// Coop.Core, GameInterface, Common and the campaign system, and nothing under DedicatedServer.*.
///
/// Opt-in: set MODDERLORDS_DEPS to a ';'-separated list of .dll paths or folders to scan.
/// </summary>
public class ModuleDependencyProbe
{
    private readonly ITestOutputHelper _out;
    public ModuleDependencyProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Report_assembly_references()
    {
        var targets = Environment.GetEnvironmentVariable("MODDERLORDS_DEPS");
        if (string.IsNullOrWhiteSpace(targets)) { _out.WriteLine("set MODDERLORDS_DEPS to run this; skipped"); return; }

        foreach (var target in targets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var files = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*.dll", SearchOption.AllDirectories).OrderBy(p => p)
                : File.Exists(target) ? new[] { target }.AsEnumerable() : Enumerable.Empty<string>();

            foreach (var file in files)
            {
                _out.WriteLine($"=== {Path.GetFileName(file)} ({new FileInfo(file).Length:N0} bytes)");
                foreach (var r in References(file)) _out.WriteLine("    " + r);
            }
        }
    }

    /// <summary>Assembly references as "Name vX.Y.Z.W", sorted. Empty when the file has no managed metadata.</summary>
    internal static IReadOnlyList<string> References(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return Array.Empty<string>();
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences
                .Select(h => md.GetAssemblyReference(h))
                .Select(a => $"{md.GetString(a.Name)} v{a.Version}")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    [Fact]
    public void Reports_nothing_for_a_file_that_is_not_managed_code()
    {
        var f = Path.Combine(Path.GetTempPath(), "mcdeps-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        File.WriteAllBytes(f, new byte[] { 0x4D, 0x5A, 0x00, 0x00 }); // an MZ header and nothing else
        try { Assert.Empty(References(f)); } finally { File.Delete(f); }
    }

    [Fact]
    public void Reads_the_references_of_a_real_assembly()
    {
        // Any assembly this test project already loaded will do; it must reference something.
        Assert.NotEmpty(References(typeof(ModderLords.Core.Compat.AssemblyScan).Assembly.Location));
    }
}
