using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// Round-trip tests for the projection, over synthetic TPAC packages. Until now this was covered only by the
/// Python spike under tools/spike, so a change to the selection rule could not be checked by `dotnet test`.
/// </summary>
public class HeadlessAssetProjectionTests
{
    private static readonly Guid PhysicsShape = Guid.Parse("e8528e0e-64b6-4e61-bae0-7569c0452aea");
    private static readonly Guid Texture = Guid.Parse("c974cbcb-5f1c-49f6-9a32-2b5b6c92c2e8");

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mcproj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Writes a minimal but structurally valid v2 package: header, then one record per entry with no metadata,
    /// no segments and no dependencies. Enough for selection to be exercised end to end.
    /// </summary>
    private static void WritePackage(string path, params (Guid Kind, string Name)[] records)
    {
        using var ms = new MemoryStream();
        var header = new byte[36];
        Encoding.ASCII.GetBytes("TPAC").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2);              // version
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), (uint)records.Length);
        ms.Write(header);
        foreach (var (kind, name) in records)
        {
            ms.Write(kind.ToByteArray());                       // type
            ms.Write(new byte[16]);                             // asset id
            ms.Write(new byte[4]);                              // v2 discriminator
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var n = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)nameBytes.Length);
            ms.Write(n); ms.Write(nameBytes);
            ms.Write(new byte[8]);                              // metadata size = 0
            ms.Write(new byte[8]);                              // unknown 8
            ms.Write(new byte[4]);                              // segment count = 0
            ms.Write(new byte[4]);                              // dependency count = 0
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static string ModuleWith(string dir, params (Guid, string)[] records)
    {
        WritePackage(Path.Combine(dir, "mod", "AssetPackages", "pack0.tpac"), records);
        return Path.Combine(dir, "mod");
    }

    [Fact]
    public void Keeps_simulation_records_and_drops_render_ones()
    {
        var dir = TempDir();
        var module = ModuleWith(dir, (PhysicsShape, "shape_a"), (Texture, "some_banner_texture"));

        var result = HeadlessAssetProjection.Prepare("Mod", module, Path.Combine(dir, "out"));

        Assert.NotNull(result);
        Assert.Equal(1, result!.AssetCount);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Keeps_the_world_map_lookup_grids_even_though_they_are_textures()
    {
        // The pair that decides which battle scene a map position produces. Without them a field battle kills the
        // client in CreateTexture2D while the server reports a healthy mission.
        var dir = TempDir();
        var module = ModuleWith(dir,
            (Texture, "worldmap_battle_scene_grid"),
            (Texture, "worldmap_colorgrade_grid_custom"),
            (Texture, "ordinary_render_texture"));

        var result = HeadlessAssetProjection.Prepare("Mod", module, Path.Combine(dir, "out"));

        Assert.NotNull(result);
        Assert.Equal(2, result!.AssetCount);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_module_with_nothing_worth_projecting_returns_null()
    {
        var dir = TempDir();
        var module = ModuleWith(dir, (Texture, "just_a_texture"));
        Assert.Null(HeadlessAssetProjection.Prepare("Mod", module, Path.Combine(dir, "out")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_module_without_asset_packages_returns_null()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "mod"));
        Assert.Null(HeadlessAssetProjection.Prepare("Mod", Path.Combine(dir, "mod"), Path.Combine(dir, "out")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Unchanged_sources_are_reused_rather_than_rebuilt()
    {
        var dir = TempDir();
        var module = ModuleWith(dir, (PhysicsShape, "shape_a"));
        var output = Path.Combine(dir, "out");

        Assert.False(HeadlessAssetProjection.Prepare("Mod", module, output)!.Reused);
        Assert.True(HeadlessAssetProjection.Prepare("Mod", module, output)!.Reused);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_cache_from_an_older_selection_rule_is_not_reused()
    {
        // The source packages never change, so without a recipe stamp a launcher that learns to project something
        // new would reuse pre-change output forever. That is exactly what happened with the world-map grids.
        var dir = TempDir();
        var module = ModuleWith(dir, (PhysicsShape, "shape_a"));
        var output = Path.Combine(dir, "out");
        Assert.False(HeadlessAssetProjection.Prepare("Mod", module, output)!.Reused);

        var marker = Path.Combine(output, "Mod", "DsAssetPackages", ".modderlords-headless-assets.json");
        Assert.True(File.Exists(marker), "the projection should have written its cache manifest");
        var json = JsonDocument.Parse(File.ReadAllText(marker));
        Assert.True(json.RootElement.TryGetProperty("Recipe", out var recipe), "the manifest must record its recipe");
        Assert.True(recipe.GetInt32() > 0);

        // Age the manifest to a previous recipe and it must rebuild rather than reuse.
        File.WriteAllText(marker, File.ReadAllText(marker).Replace($"\"Recipe\":{recipe.GetInt32()}", "\"Recipe\":1")
                                                          .Replace($"\"Recipe\": {recipe.GetInt32()}", "\"Recipe\": 1"));
        Assert.False(HeadlessAssetProjection.Prepare("Mod", module, output)!.Reused);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_changed_source_package_is_rebuilt()
    {
        var dir = TempDir();
        var module = ModuleWith(dir, (PhysicsShape, "shape_a"));
        var output = Path.Combine(dir, "out");
        Assert.False(HeadlessAssetProjection.Prepare("Mod", module, output)!.Reused);

        WritePackage(Path.Combine(module, "AssetPackages", "pack0.tpac"),
            (PhysicsShape, "shape_a"), (PhysicsShape, "shape_b"));
        var again = HeadlessAssetProjection.Prepare("Mod", module, output);
        Assert.False(again!.Reused);
        Assert.Equal(2, again.AssetCount);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Inventory_names_every_record_and_says_which_survive()
    {
        var dir = TempDir();
        var module = ModuleWith(dir, (PhysicsShape, "shape_a"), (Texture, "worldmap_battle_scene_grid"), (Texture, "render_me"));

        var entries = HeadlessAssetProjection.Inventory(Path.Combine(module, "AssetPackages"));

        Assert.Equal(3, entries.Count);
        Assert.True(entries.Single(e => e.Name == "worldmap_battle_scene_grid").IsSimulation);
        Assert.False(entries.Single(e => e.Name == "render_me").IsSimulation);
        Assert.Equal("Texture", entries.Single(e => e.Name == "render_me").Type);
        Assert.Equal("PhysicsShape", entries.Single(e => e.Name == "shape_a").Type);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_package_that_is_not_a_tpac_is_refused()
    {
        var dir = TempDir();
        var packages = Path.Combine(dir, "mod", "AssetPackages");
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, "pack0.tpac"), "this is not a package");

        Assert.ThrowsAny<Exception>(() => HeadlessAssetProjection.Prepare("Mod", Path.Combine(dir, "mod"), Path.Combine(dir, "out")));
        Directory.Delete(dir, true);
    }
}
