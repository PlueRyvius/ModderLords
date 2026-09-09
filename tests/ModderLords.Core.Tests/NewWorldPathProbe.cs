using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModderLords.Coop.Launch;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Diagnostic: what would actually be required to start a NEW campaign inside the dedicated server process.
///
/// The earlier <see cref="WorldGenerationProbe"/> answered "is the obvious entry point reachable" (no). This one asks
/// the harder question — what does the whole path look like, how much of it is public, and what does it drag in — so
/// the cost of doing it anyway can be judged rather than guessed. Metadata only; nothing is loaded or invoked.
/// </summary>
public class NewWorldPathProbe
{
    private readonly ITestOutputHelper _out;
    public NewWorldPathProbe(ITestOutputHelper o) => _out = o;

    /// <summary>Types on the "start a new campaign" path, wherever they live.</summary>
    private static readonly string[] Types =
    {
        "SandBoxGameManager", "MBGameManager", "GameManagerBase", "CharacterCreationContent",
        "CharacterCreationState", "CharacterCreation", "SandBoxCharacterCreationContent", "Campaign",
        "CampaignGameStarter", "SaveHandler", "SandBoxSaveManager", "MBSaveLoad", "SaveManager",
        "CampaignCreatorDelegate", "SandBoxGameCreator", "GameCreator", "CampaignGameMode",
    };

    [Fact]
    public void Report_new_campaign_path()
    {
        var root = ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault();
        if (root is null) { _out.WriteLine("no DedicatedServer package; skipped"); return; }
        var engine = Path.Combine(ServerPaths.Create(root).DedicatedServerRoot, "engine");
        var coopBin = Path.Combine(engine, "Modules", "Coop", "bin", "Win64_Shipping_Server");
        var serverBin = Path.Combine(engine, "bin", "Win64_Shipping_Server");

        foreach (var dll in new[]
                 {
                     Path.Combine(coopBin, "SandBox.dll"),
                     Path.Combine(serverBin, "TaleWorlds.CampaignSystem.dll"),
                     Path.Combine(serverBin, "TaleWorlds.Core.dll"),
                     Path.Combine(serverBin, "TaleWorlds.MountAndBlade.dll"),
                     Path.Combine(serverBin, "TaleWorlds.SaveSystem.dll"),
                 })
        {
            if (!File.Exists(dll)) { _out.WriteLine($"===== {Path.GetFileName(dll)}: NOT PRESENT ====="); continue; }
            _out.WriteLine($"===== {Path.GetFileName(dll)} =====");
            Report(dll);
        }
    }

    private void Report(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var name = md.GetString(t.Name);
            if (!Types.Any(x => name.Equals(x, StringComparison.Ordinal))) continue;

            var vis = t.Attributes & TypeAttributes.VisibilityMask;
            _out.WriteLine($"  type {md.GetString(t.Namespace)}.{name} [{vis}]");

            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mn = md.GetString(m.Name);
                // Only the members that could plausibly start or save a game; the full surface is far too noisy.
                var isDelegate = name.Contains("Delegate", StringComparison.Ordinal) || name.Contains("Creator", StringComparison.Ordinal);
                if (!isDelegate && !(mn.Contains("New", StringComparison.OrdinalIgnoreCase)
                      || mn.Contains("Start", StringComparison.OrdinalIgnoreCase)
                      || mn.Contains("Launch", StringComparison.OrdinalIgnoreCase)
                      || mn.Contains("Save", StringComparison.OrdinalIgnoreCase)
                      || mn.Contains("Load", StringComparison.OrdinalIgnoreCase)
                      || mn == ".ctor")) continue;

                var acc = m.Attributes & MethodAttributes.MemberAccessMask;
                var isStatic = (m.Attributes & MethodAttributes.Static) != 0;
                var sig = m.DecodeSignature(new SigNames(), null!);
                _out.WriteLine($"      {(acc == MethodAttributes.Public ? "PUBLIC " : acc + " ")}{(isStatic ? "static " : "")}" +
                    $"{sig.ReturnType} {mn}({string.Join(", ", sig.ParameterTypes)})");
            }
        }
    }

    private sealed class SigNames : ISignatureTypeProvider<string, object>
    {
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte k) => "?";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericMethodParameter(object g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object g, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
    }
}
