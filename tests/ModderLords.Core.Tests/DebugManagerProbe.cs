using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Diagnostic for Phase 3: the exact shape of TaleWorlds.Library.IDebugManager, which the compat module has to
/// implement in full in order to decorate the one DedicatedServer.Core already installs. Metadata only — nothing is
/// loaded or invoked. Kept because a game update that adds a member to this interface breaks the decorator at
/// compile time, and this says what changed.
/// </summary>
public class DebugManagerProbe
{
    private readonly ITestOutputHelper _out;
    public DebugManagerProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Report_debug_manager_surface()
    {
        var pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "bannerlord.referenceassemblies.core");
        if (!Directory.Exists(pkg)) { _out.WriteLine("reference assemblies not restored; skipped"); return; }

        // The asset-failure modals are raised natively, not through IDebugManager, so the engine-side toggles that
        // decide whether a warning aborts the process are the seam that can actually reach them.
        var engine = Directory.EnumerateFiles(pkg, "TaleWorlds.Engine.dll", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("net472", StringComparison.OrdinalIgnoreCase));
        if (engine is not null) ReportEngineToggles(engine);

        var dll = Directory.EnumerateFiles(pkg, "TaleWorlds.Library.dll", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("net472", StringComparison.OrdinalIgnoreCase));
        if (dll is null) { _out.WriteLine("no net472 TaleWorlds.Library.dll; skipped"); return; }
        _out.WriteLine(dll);

        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var name = md.GetString(t.Name);
            if (name is not ("IDebugManager" or "Debug")) continue;
            _out.WriteLine($"=== {md.GetString(t.Namespace)}.{name} [{t.Attributes & TypeAttributes.VisibilityMask}] ===");

            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var sig = m.DecodeSignature(new Sig(), null!);
                var mn = md.GetString(m.Name);
                _out.WriteLine($"  {sig.ReturnType} {mn}({string.Join(", ", sig.ParameterTypes)})" +
                    $"  [{m.Attributes & MethodAttributes.MemberAccessMask}{((m.Attributes & MethodAttributes.Static) != 0 ? ", Static" : "")}]");
            }
            foreach (var ph in t.GetProperties())
                _out.WriteLine("  property " + md.GetString(md.GetPropertyDefinition(ph).Name));
        }
    }

    private static readonly string[] Toggles =
    {
        "SetAssertionsAndWarningsSetExitCode", "SetCrashOnAsserts", "SilentAssert", "SetTestMode",
        "IgnoreWarning", "SetIgnore", "ExitCode", "DisableWarning", "SetDebugMode",
    };

    private void ReportEngineToggles(string dll)
    {
        _out.WriteLine("=== TaleWorlds.Engine toggles ===");
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mn = md.GetString(m.Name);
                if (!Toggles.Any(x => mn.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                var sig = m.DecodeSignature(new Sig(), null!);
                _out.WriteLine($"  {md.GetString(t.Namespace)}.{md.GetString(t.Name)}: {sig.ReturnType} {mn}({string.Join(", ", sig.ParameterTypes)})" +
                    $"  [{m.Attributes & MethodAttributes.MemberAccessMask}{((m.Attributes & MethodAttributes.Static) != 0 ? ", Static" : "")}]");
            }
        }
    }

    /// <summary>Prints signature types as source-ish names; enough to write the forwarding members by hand.</summary>
    private sealed class Sig : ISignatureTypeProvider<string, object>
    {
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => $"{g}<{string.Join(", ", a)}>";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawTypeKind) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawTypeKind) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte rawTypeKind) => "?";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericMethodParameter(object g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object g, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool isRequired) => u;
        public string GetPinnedType(string e) => e;
    }
}
