using System.Runtime.Versioning;
using Bannerlord.ModuleManager;
using ModderLords.Core.Compat;
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

    private static ScanResult Scanned(ServerVerdict verdict, params string[] ui) =>
        new("x", verdict, ui, [], [], verdict == ServerVerdict.NeedsReview ? ["constructs UI objects: GauntletLayer"] : [], [], [], [], false);

    /// <summary>
    /// TAOM's shape: the mod declares itself client-only, the user (or the default) picks Run anyway, and its code
    /// reaches for the render stack. The plan has to say so up front - the failure mode is a silent engine death.
    /// </summary>
    [Fact]
    public void Run_over_a_client_only_tag_warns_when_the_code_needs_the_render_stack()
    {
        var mod = MakeMod("ViewBound", serverBin: true, clientOnlyTags: true);
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            _ => Scanned(ServerVerdict.NeedsReview, "SandBox.View", "SandBox.GauntletUI"), _ => null);
        var notes = plan.Entries.Single().Notes;
        Assert.Contains(notes, n => n.StartsWith("WARNING:") && n.Contains("SandBox.View") && n.Contains("DependencyOnly"));
        Assert.Contains(notes, n => n.Contains("GauntletLayer"));
    }

    /// <summary>
    /// ImprovedGarrisons' shape: client-only tag, view references all over its IL, and it still reaches SERVING behind
    /// the bundled guards. A curated Run record is a tested result, so the static scan must not second-guess it.
    /// </summary>
    [Fact]
    public void A_compat_record_that_says_Run_suppresses_the_warning()
    {
        var mod = MakeMod("Vouched", serverBin: true, clientOnlyTags: true);
        var vouched = new CompatRecord { Id = "Vouched", DefaultRole = ServerRole.Run, Verdict = CompatVerdict.NeedsRecipe };
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            _ => Scanned(ServerVerdict.NeedsReview, "SandBox.View"), _ => vouched);
        Assert.DoesNotContain(plan.Entries.Single().Notes, n => n.StartsWith("WARNING:"));
    }

    /// <summary>A record that does not endorse running it (DependencyOnly) leaves the warning in place.</summary>
    [Fact]
    public void A_DependencyOnly_record_does_not_suppress_the_warning()
    {
        var mod = MakeMod("NotVouched", serverBin: true, clientOnlyTags: true);
        var rec = new CompatRecord { Id = "NotVouched", DefaultRole = ServerRole.DependencyOnly, Verdict = CompatVerdict.NeedsRecipe };
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            _ => Scanned(ServerVerdict.NeedsReview, "SandBox.View"), _ => rec);
        Assert.Contains(plan.Entries.Single().Notes, n => n.StartsWith("WARNING:"));
    }

    [Fact]
    public void Run_over_a_client_only_tag_stays_quiet_for_server_safe_code()
    {
        var mod = MakeMod("Harmless", serverBin: true, clientOnlyTags: true);
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            _ => Scanned(ServerVerdict.ServerSafe), _ => null);
        Assert.DoesNotContain(plan.Entries.Single().Notes, n => n.StartsWith("WARNING:"));
    }

    /// <summary>A mod with no headless-exclusion tags is never scanned - Run is what its own manifest asked for.</summary>
    [Fact]
    public void Mods_without_client_only_tags_are_not_scanned()
    {
        var mod = MakeMod("Plain", serverBin: true, clientOnlyTags: false);
        OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            _ => throw new Exception("should not be scanned"), _ => null);
    }

    /// <summary>DependencyOnly on a mod with code always shadows, so the manifest can actually be rewritten.</summary>
    [Fact]
    public void DependencyOnly_shadows_and_strips_the_submodule()
    {
        var mod = MakeMod("DataMod", serverBin: true, clientOnlyTags: true);
        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.DependencyOnly)]);
        var entry = plan.Entries.Single();
        Assert.Equal(OverlayKind.Shadow, entry.Kind);
        Assert.Contains(entry.Notes, n => n.Contains("dependency-only"));

        Assert.Single(new OverlayApplier().Apply(plan).Applied);
        var written = File.ReadAllText(Path.Combine(_engineModules, "DataMod", "SubModule.xml"));
        Assert.DoesNotContain("DataMod.Sub", written);
        Assert.Contains("<Id value=\"DataMod\"", written);
        Assert.Contains("<Version value=\"v1.2.3\"", written);
        new OverlayApplier().RemoveAll(_overlay);
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

    [Fact]
    public void Headless_projection_excludes_client_assets_and_repoints_the_map()
    {
        var mod = MakeMod("Projected", serverBin: false, clientOnlyTags: true);
        Directory.CreateDirectory(Path.Combine(mod.FolderPath, "AssetPackages"));
        File.WriteAllText(Path.Combine(mod.FolderPath, "AssetPackages", "client.tpac"), "client");
        Directory.CreateDirectory(Path.Combine(mod.FolderPath, "SceneObj", "Main_map"));
        Directory.CreateDirectory(Path.Combine(mod.FolderPath, "SceneObj", "Backups"));
        File.WriteAllText(Path.Combine(mod.FolderPath, "SceneObj", "Main_map", "scene.xscene"), "client-scene");

        var generatedAssets = Path.Combine(_root, "generated", "DsAssetPackages");
        var generatedMap = Path.Combine(_root, "generated", "Main_map");
        Directory.CreateDirectory(generatedAssets);
        Directory.CreateDirectory(generatedMap);
        File.WriteAllText(Path.Combine(generatedAssets, "server.tpac"), "server");
        File.WriteAllText(Path.Combine(generatedMap, "modderlords-map.xml"), "<map />");

        var plan = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            headlessAssetPaths: new Dictionary<string, string> { [mod.Id] = generatedAssets },
            headlessMapPaths: new Dictionary<string, string> { [mod.Id] = generatedMap });
        var result = new OverlayApplier().Apply(plan);
        Assert.Empty(result.Warnings);

        var shadow = plan.Entries.Single().ShadowPath!;
        Assert.False(Directory.Exists(Path.Combine(shadow, "AssetPackages")));
        Assert.True(Junction.PointsTo(Path.Combine(shadow, "DsAssetPackages"), generatedAssets));
        Assert.True(Junction.PointsTo(Path.Combine(shadow, "SceneObj", "Main_map"), generatedMap));
        Assert.False(Directory.Exists(Path.Combine(shadow, "SceneObj", "Backups")));
        Assert.True(File.Exists(Path.Combine(mod.FolderPath, "AssetPackages", "client.tpac")));
    }
}
