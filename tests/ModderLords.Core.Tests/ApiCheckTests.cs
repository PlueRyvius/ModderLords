using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ModderLords.Core.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The game API check before a launch: a mod's calls into a game that no longer has them are found by reading
/// metadata. The assemblies here are built in the test, a stand-in "game" and a "mod" compiled against an older one.
/// </summary>
public sealed class ApiCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ml-apicheck-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_call_the_game_no_longer_has_is_found_by_type_name_and_parameter_count()
    {
        var game = ApiIndex.Of([Game()]);
        var missing = ApiCheck.Missing(Mod(), game, ["TaleWorlds."]);
        Assert.Equal(
        [
            "method TaleWorlds.CampaignSystem.Campaign::.ctor (1 parameter)",   // v1.5.7 added the start options
            "method TaleWorlds.CampaignSystem.Campaign::Removed (0 parameters)",
            "type TaleWorlds.CampaignSystem.Gone",
        ], missing);
    }

    [Fact]
    public void Inherited_members_and_other_namespaces_are_not_reported()
    {
        // Mod() also calls Campaign(int, int), the field Mode, Run() through the subclass CampaignChild, and
        // System.Object's constructor, none of which is missing.
        var missing = ApiCheck.Missing(Mod(), ApiIndex.Of([Game()]), ["TaleWorlds."]);
        Assert.DoesNotContain(missing, m => m.EndsWith("::Mode", StringComparison.Ordinal) || m.Contains("::Run ") || m.Contains("(2 parameters)") || m.Contains(" System.Object"));
        Assert.Equal(3, missing.Count);
    }

    [Fact]
    public void Only_dlls_a_module_loads_are_checked()
    {
        // The module names Mod.dll, which references Helper.dll; Mod.v1.2.6.dll is a build for another game version
        // that a loader would pick by name at runtime, and nothing references it.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        Build(Path.Combine(bin, "Helper.dll"), "Helper", (_, _) => { });
        Build(Path.Combine(bin, "Mod.dll"), "Mod", (mb, _) => mb.AddAssemblyReference(mb.GetOrAddString("Helper"), new Version(1, 0, 0, 0), default, default, 0, default));
        Build(Path.Combine(bin, "Mod.v1.2.6.dll"), "Mod.v1.2.6", (_, _) => { });
        Assert.Equal(["Mod.dll", "Helper.dll"], ApiCheck.LoadedDlls(bin, ["Mod.dll"]).Select(Path.GetFileName));
    }

    [Fact]
    public void A_dll_that_is_not_dotnet_is_skipped()
    {
        var native = Path.Combine(_root, "native.dll");
        File.WriteAllBytes(native, [0x4D, 0x5A, 0, 0]);
        Assert.Empty(ApiCheck.CheckAll([native], ApiIndex.Of([Game()])));
    }

    // ---- the assemblies -------------------------------------------------------------------------------------------

    /// <summary>The "game": Campaign(int, int), field Mode, Run(), and CampaignChild : Campaign. No Campaign(int).</summary>
    private string Game() => Build(Path.Combine(_root, "game", "TaleWorlds.CampaignSystem.dll"), "TaleWorlds.CampaignSystem", (mb, obj) =>
    {
        var ns = mb.GetOrAddString("TaleWorlds.CampaignSystem");
        var campaign = mb.AddTypeDefinition(TypeAttributes.Public, ns, mb.GetOrAddString("Campaign"), obj,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        mb.AddFieldDefinition(FieldAttributes.Public, mb.GetOrAddString("Mode"), mb.GetOrAddBlob(FieldSig()));
        mb.AddMethodDefinition(MethodAttributes.Public, MethodImplAttributes.IL, mb.GetOrAddString(".ctor"), mb.GetOrAddBlob(MethodSig(2)), -1, default);
        mb.AddMethodDefinition(MethodAttributes.Public, MethodImplAttributes.IL, mb.GetOrAddString("Run"), mb.GetOrAddBlob(MethodSig(0)), -1, default);
        mb.AddTypeDefinition(TypeAttributes.Public, ns, mb.GetOrAddString("CampaignChild"), campaign,
            MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(3));
    });

    /// <summary>The "mod", built against an older game: calls into Campaign, CampaignChild and a type that is gone.</summary>
    private string Mod() => Build(Path.Combine(_root, "mod", "Mod.dll"), "Mod", (mb, obj) =>
    {
        var gameRef = mb.AddAssemblyReference(mb.GetOrAddString("TaleWorlds.CampaignSystem"), new Version(1, 0, 0, 0), default, default, 0, default);
        var ns = mb.GetOrAddString("TaleWorlds.CampaignSystem");
        var campaign = mb.AddTypeReference(gameRef, ns, mb.GetOrAddString("Campaign"));
        var child = mb.AddTypeReference(gameRef, ns, mb.GetOrAddString("CampaignChild"));
        mb.AddTypeReference(gameRef, ns, mb.GetOrAddString("Gone"));
        mb.AddMemberReference(campaign, mb.GetOrAddString(".ctor"), mb.GetOrAddBlob(MethodSig(1)));
        mb.AddMemberReference(campaign, mb.GetOrAddString(".ctor"), mb.GetOrAddBlob(MethodSig(2)));
        mb.AddMemberReference(campaign, mb.GetOrAddString("Removed"), mb.GetOrAddBlob(MethodSig(0)));
        mb.AddMemberReference(campaign, mb.GetOrAddString("Mode"), mb.GetOrAddBlob(FieldSig()));
        mb.AddMemberReference(child, mb.GetOrAddString("Run"), mb.GetOrAddBlob(MethodSig(0)));
        mb.AddMemberReference(obj, mb.GetOrAddString(".ctor"), mb.GetOrAddBlob(MethodSig(0)));
    });

    private static BlobBuilder MethodSig(int parameters)
    {
        var blob = new BlobBuilder();
        new BlobEncoder(blob).MethodSignature(isInstanceMethod: true).Parameters(parameters, r => r.Void(), p =>
        {
            for (int i = 0; i < parameters; i++) p.AddParameter().Type().Int32();
        });
        return blob;
    }

    private static BlobBuilder FieldSig()
    {
        var blob = new BlobBuilder();
        new BlobEncoder(blob).FieldSignature().Int32();
        return blob;
    }

    /// <summary>A minimal library assembly at <paramref name="path"/>: module, assembly, mscorlib and what <paramref name="build"/> adds.</summary>
    private static string Build(string path, string name, Action<MetadataBuilder, TypeReferenceHandle> build)
    {
        var mb = new MetadataBuilder();
        mb.AddModule(0, mb.GetOrAddString(name + ".dll"), mb.GetOrAddGuid(Guid.NewGuid()), default, default);
        mb.AddAssembly(mb.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var corlib = mb.AddAssemblyReference(mb.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default, default, 0, default);
        var obj = mb.AddTypeReference(corlib, mb.GetOrAddString("System"), mb.GetOrAddString("Object"));
        mb.AddTypeDefinition(default, default, mb.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        build(mb, obj);
        var pe = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(mb), new BlobBuilder());
        var image = new BlobBuilder();
        pe.Serialize(image);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }
}
