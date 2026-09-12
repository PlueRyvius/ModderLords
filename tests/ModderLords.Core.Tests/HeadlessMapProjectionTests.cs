using System.Xml.Linq;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

public class HeadlessMapProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "modderlords-projection-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Shaped like a real Main_map: the two bounds markers are transform-only entities, and everything else in
    /// &lt;entities&gt; is renderable content the projection is there to strip.
    /// </summary>
    private string WriteSourceScene()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "navmesh.bin", "terrain.bin", "flora.bin", "atmosphere.xml" })
            File.WriteAllText(Path.Combine(source, name), name);
        File.WriteAllText(Path.Combine(source, "scene.xscene"), """
<scene>
  <terrain node_size="100.000" node_dimension_x="16" node_dimension_y="16" />
  <entities>
    <game_entity name="border_min"><transform position="62.000, 0.000, 26.000" /></game_entity>
    <game_entity name="border_max"><transform position="1700.000, 1550.000, 646.010" /></game_entity>
    <game_entity name="some_castle"><transform position="500.000, 500.000, 40.000" /></game_entity>
    <game_entity name="some_tree"><transform position="600.000, 600.000, 41.000" /></game_entity>
  </entities>
</scene>
""");
        return source;
    }

    private static XElement Entities(string sceneDir) =>
        XDocument.Load(Path.Combine(sceneDir, "scene.xscene")).Root!.Element("entities")!;

    [Fact]
    public void Keeps_the_bounds_markers_and_strips_everything_else()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");

        HeadlessMapProjection.Prepare(source, output, keepTerrain: false);

        // A client reads its map's bounds out of these two entities. Dropping them is the only reason the
        // projection has to publish the same numbers in a sidecar.
        var names = Entities(output).Elements("game_entity").Select(e => (string?)e.Attribute("name")).ToList();
        Assert.Equal(new[] { "border_min", "border_max" }, names);
    }

    [Fact]
    public void The_kept_markers_carry_their_original_positions()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");

        HeadlessMapProjection.Prepare(source, output, keepTerrain: false);

        var max = Entities(output).Elements("game_entity").Single(e => (string?)e.Attribute("name") == "border_max");
        Assert.Equal("1700.000, 1550.000, 646.010", (string?)max.Element("transform")!.Attribute("position"));
    }

    [Fact]
    public void Removes_the_terrain_descriptor_unless_asked_to_keep_it()
    {
        var source = WriteSourceScene();

        HeadlessMapProjection.Prepare(source, Path.Combine(_root, "stripped"), keepTerrain: false);
        HeadlessMapProjection.Prepare(source, Path.Combine(_root, "kept"), keepTerrain: true);

        Assert.Null(XDocument.Load(Path.Combine(_root, "stripped", "scene.xscene")).Root!.Element("terrain"));
        Assert.NotNull(XDocument.Load(Path.Combine(_root, "kept", "scene.xscene")).Root!.Element("terrain"));
    }

    [Fact]
    public void Reuses_a_projection_built_under_the_same_rule()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");

        Assert.False(HeadlessMapProjection.Prepare(source, output, keepTerrain: false).Reused);
        Assert.True(HeadlessMapProjection.Prepare(source, output, keepTerrain: false).Reused);
    }

    [Fact]
    public void Rebuilds_rather_than_reusing_a_projection_built_under_a_different_rule()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");
        HeadlessMapProjection.Prepare(source, output, keepTerrain: false);

        // Silently reusing a scene built under the other rule is how a flipped switch comes to look like it did
        // nothing at all.
        Assert.False(HeadlessMapProjection.Prepare(source, output, keepTerrain: true).Reused);
    }

    [Fact]
    public void Rebuilds_a_projection_from_before_the_bounds_markers_were_kept()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");
        HeadlessMapProjection.Prepare(source, output, keepTerrain: false);

        // An older cache has no record of the markers, and reusing it would serve a scene without them.
        var marker = Path.Combine(output, ".modderlords-headless-map");
        var stamp = File.ReadAllText(marker).Replace("\"kept_bounds_entities\": 2,", "");
        File.WriteAllText(marker, stamp);

        Assert.False(HeadlessMapProjection.Prepare(source, output, keepTerrain: false).Reused);
    }

    [Fact]
    public void Publishes_the_bounds_it_read_from_the_scene()
    {
        var source = WriteSourceScene();
        var output = Path.Combine(_root, "out");

        var result = HeadlessMapProjection.Prepare(source, output, keepTerrain: false);

        // All three components: HeadlessMapExperiment parses these as a 3-vector and takes the height from the
        // maximum's z, exactly as a client takes it from the border_max entity's global frame.
        var map = XDocument.Load(result.MetadataPath).Root!;
        Assert.Equal("62,0,26", (string?)map.Attribute("border_min"));
        Assert.Equal("1700,1550,646.01", (string?)map.Attribute("border_max"));
        Assert.Equal("1600,1600", (string?)map.Attribute("terrain-size"));
    }
}
