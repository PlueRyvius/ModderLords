using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Coop.Launch;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Any mod that replaces the campaign map gets the server map TAOM gets. The scene numbers are Empires of Europe
/// 1100's: 16 x 16 terrain nodes of 106, twice the vanilla 848 a side, which is what every party past 848 fell off.
/// </summary>
public sealed class MapModTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbc-map-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, true);

    private ModSelection Mod(string id, bool map, bool completeScene = true)
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"),
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Module><Id value=\"{id}\"/><Name value=\"{id}\"/><Version value=\"v1.0.0\"/><SubModules/></Module>");
        if (map)
        {
            var scene = Path.Combine(folder, "SceneObj", "Main_map");
            Directory.CreateDirectory(scene);
            File.WriteAllText(Path.Combine(scene, "scene.xscene"), """
                <scene name="Main_map">
                  <entities>
                    <game_entity name="border_max"><transform position="1636.020, 1530.790, 464.460"/></game_entity>
                    <game_entity name="border_min"><transform position="36.578, 569.764, 0.000"/></game_entity>
                    <game_entity name="a_tree"><transform position="1, 2, 3"/></game_entity>
                  </entities>
                  <terrain enabled="true" node_dimension_x="16" node_dimension_y="16" node_size="106.000"/>
                </scene>
                """);
            if (completeScene)
                foreach (var name in new[] { "navmesh.bin", "terrain.bin", "flora.bin", "atmosphere.xml" })
                    File.WriteAllText(Path.Combine(scene, name), name);
        }
        var module = ModuleCatalog.TryParse(folder, ModuleSourceKind.Custom, out var problem) ?? throw new Exception(problem);
        return new ModSelection(module, ServerRole.Run);
    }

    [Fact]
    public void A_mod_with_its_own_campaign_map_is_given_a_server_copy_carrying_that_maps_size()
    {
        var messages = new List<string>();
        var content = LaunchSession.PrepareHeadlessContent(Path.Combine(_root, "overlay"),
            [Mod("Plain", map: false), Mod("Europe1100", map: true)], applySideEffects: true, messages);

        var path = Assert.Single(content.MapPaths);
        Assert.Equal("Europe1100", path.Key);
        Assert.Empty(content.AssetPaths);
        var sidecar = File.ReadAllText(Path.Combine(path.Value, "modderlords-map.xml"));
        Assert.Contains("terrain-size=\"1696,1696\"", sidecar);
        Assert.Contains("border_max=\"1636.02,1530.79,464.46\"", sidecar);
        Assert.DoesNotContain("a_tree", File.ReadAllText(Path.Combine(path.Value, "scene.xscene")));
        Assert.Contains(messages, m => m.Contains("Europe1100's campaign map"));

        var env = LaunchSession.HeadlessMapEnvironment(content.MapPaths);
        Assert.Equal("Europe1100", env["MODDERLORDS_HEADLESS_MAP_MODULE"]);
        Assert.Equal(Path.Combine(path.Value, "modderlords-map.xml"), env["MODDERLORDS_HEADLESS_MAP"]);
    }

    [Fact]
    public void Mods_without_a_map_change_nothing()
    {
        var messages = new List<string>();
        var content = LaunchSession.PrepareHeadlessContent(Path.Combine(_root, "overlay"),
            [Mod("Plain", map: false)], applySideEffects: true, messages);
        Assert.Empty(content.MapPaths);
        Assert.Empty(messages);
        Assert.Empty(LaunchSession.HeadlessMapEnvironment(content.MapPaths));
    }

    [Fact]
    public void A_preview_prepares_nothing()
    {
        var content = LaunchSession.PrepareHeadlessContent(Path.Combine(_root, "overlay"),
            [Mod("Europe1100", map: true)], applySideEffects: false, []);
        Assert.Empty(content.MapPaths);
        Assert.False(Directory.Exists(Path.Combine(_root, "overlay")));
    }

    [Fact]
    public void A_map_that_cannot_be_prepared_warns_and_the_launch_goes_on_as_before()
    {
        var messages = new List<string>();
        var content = LaunchSession.PrepareHeadlessContent(Path.Combine(_root, "overlay"),
            [Mod("HalfMap", map: true, completeScene: false)], applySideEffects: true, messages);
        Assert.Empty(content.MapPaths);
        var warning = Assert.Single(messages);
        Assert.StartsWith("WARNING HalfMap replaces the campaign map", warning);
    }
}
