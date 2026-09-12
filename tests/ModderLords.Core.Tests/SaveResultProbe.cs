using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Diagnostic: the values of the engine's save-result enum, so a "save callback=N" line from a failed
/// world-creation run can be read without guessing. Metadata only — nothing is loaded or invoked.
/// </summary>
public class SaveResultProbe
{
    private readonly ITestOutputHelper _out;
    public SaveResultProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Report_save_result_values()
    {
        var pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "bannerlord.referenceassemblies.core");
        if (!Directory.Exists(pkg)) { _out.WriteLine("reference assemblies not restored; skipped"); return; }

        foreach (var dll in Directory.EnumerateFiles(pkg, "TaleWorlds.*.dll", SearchOption.AllDirectories)
                     .Where(p => p.Contains("net472", StringComparison.OrdinalIgnoreCase)))
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                var name = md.GetString(t.Name);
                if (!name.Contains("SaveResult", StringComparison.OrdinalIgnoreCase)) continue;
                _out.WriteLine($"=== {md.GetString(t.Namespace)}.{name}  ({Path.GetFileName(dll)}) ===");
                foreach (var fh in t.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    if ((f.Attributes & FieldAttributes.Static) == 0) continue;
                    var dc = f.GetDefaultValue();
                    var val = dc.IsNil ? "?" : md.GetConstant(dc).Value is var _
                        ? BitConverter.ToString(md.GetBlobBytes(md.GetConstant(dc).Value)) : "?";
                    _out.WriteLine($"  {md.GetString(f.Name)} = {val}");
                }
            }
        }
    }
}
