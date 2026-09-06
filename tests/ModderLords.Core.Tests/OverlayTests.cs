using System.Runtime.Versioning;
using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>End-to-end overlay on a throwaway folder tree: real junctions, real manifests. Windows only (junctions).</summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbc-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _engineModules;
    private readonly string _overlay;

    public OverlayTests()
    {
        _engineModules = Path.Combine(_root, "engine", "Modules");
        _overlay = Path.Combine(_root, "overlay");
        Directory.CreateDirectory(_engineModules);
    }

    public void Dispose()
    {
        // Junctions must be removed as links, never recursed into.
        foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (Junction.IsJunction(d)) Directory.Delete(d, false);
        Directory.Delete(_root, true);
    }

    private DiscoveredModule MakeMod(string id, bool serverBin, bool clientOnlyTags)
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(Path.Combine(folder, "bin", serverBin ? "Win64_Shipping_Server" : "Win64_Shipping_Client"));
        Directory.CreateDirectory(Path.Combine(folder, "ModuleData"));
        File.WriteAllText(Path.Combine(folder, "ModuleData", "data.xml"), "<x/>");
        File.WriteAllText(Path.Combine(folder, "bin", serverBin ? "Win64_Shipping_Server" : "Win64_Shipping_Client", id + ".dll"), "not really");
        var tags = clientOnlyTags ? "<Tags><Tag key=\"DedicatedServerType\" value=\"none\"/></Tags>" : "";
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Module><Id value="{id}"/><Name value="{id}"/><Version value="v1.2.3"/>
            <SubModules><SubModule><Name value="{id}"/><DLLName value="{id}.dll"/><SubModuleClassType value="{id}.Sub"/>{tags}</SubModule></SubModules></Module>
            """);
        return ModuleCatalog.TryParse(folder, ModuleSourceKind.Custom, out var problem) ?? throw new Exception(problem);
    }

    [Fact]
    public void Junction_create_repoint_remove()
    {
        var a = Path.Combine(_root, "a"); var b = Path.Combine(_root, "b"); var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "f.txt"), "a");
        Junction.Create(link, a);
        Assert.True(Junction.IsJunction(link));
        Assert.True(Junction.PointsTo(link, a));
        Assert.Equal("a", File.ReadAllText(Path.Combine(link, "f.txt")));
        Junction.Create(link, b);   // repoint
        Assert.True(Junction.PointsTo(link, b));
        Junction.Remove(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(a, "f.txt")));   // target untouched
        Assert.Throws<IOException>(() => Junction.Create(a, b));   // never replace a real folder
    }

    [Fact]
    public void Direct_junction_for_server_ready_mod_and_shadow_for_client_only_mod()
    {
        var ready = MakeMod("Ready", serverBin: true, clientOnlyTags: false);
        var clientOnly = MakeMod("ClientOnly", serverBin: false, clientOnlyTags: true);
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(ready, ServerRole.Run), new ModSelection(clientOnly, ServerRole.Run)]);
        Assert.Equal(OverlayKind.DirectJunction, plan.Entries[0].Kind);
        Assert.Equal(OverlayKind.Shadow, plan.Entries[1].Kind);

        var result = new OverlayApplier().Apply(plan);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Applied.Count);

        // Engine sees both under engine\Modules with a server bin and the DLL inside it.
        Assert.True(Junction.PointsTo(Path.Combine(_engineModules, "Ready"), ready.FolderPath));
        Assert.True(File.Exists(Path.Combine(_engineModules, "ClientOnly", "bin", "Win64_Shipping_Server", "ClientOnly.dll")));
        Assert.True(File.Exists(Path.Combine(_engineModules, "ClientOnly", "ModuleData", "data.xml")));
        var shadowManifest = File.ReadAllText(Path.Combine(_engineModules, "ClientOnly", "SubModule.xml"));
        Assert.DoesNotContain("DedicatedServerType", shadowManifest);
        Assert.Contains("<Id value=\"ClientOnly\"", shadowManifest);
        // The mod folder itself was not modified.
        Assert.Contains("DedicatedServerType", File.ReadAllText(Path.Combine(clientOnly.FolderPath, "SubModule.xml")));

        // Dropping a mod removes exactly its link.
        var plan2 = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(ready, ServerRole.Run)]);
        var result2 = new OverlayApplier().Apply(plan2);
        Assert.Single(result2.Removed);
        Assert.False(Directory.Exists(Path.Combine(_engineModules, "ClientOnly")));
        Assert.True(Directory.Exists(clientOnly.FolderPath));

        // RemoveAll cleans the rest and is idempotent.
        Assert.Single(new OverlayApplier().RemoveAll(_overlay));
        Assert.Empty(new OverlayApplier().RemoveAll(_overlay));
    }

    [Fact]
    public void A_real_folder_in_engine_modules_is_never_replaced()
    {
        var mod = MakeMod("Taken", serverBin: true, clientOnlyTags: false);
        Directory.CreateDirectory(Path.Combine(_engineModules, "Taken"));
        var result = new OverlayApplier().Apply(OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)]));
        Assert.Empty(result.Applied);
        Assert.Contains(result.Warnings, w => w.Contains("real folder"));
        Assert.False(Junction.IsJunction(Path.Combine(_engineModules, "Taken")));
    }
}
