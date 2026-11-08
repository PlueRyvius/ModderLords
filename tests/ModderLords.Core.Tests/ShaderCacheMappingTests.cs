using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

public class ShaderCacheMappingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-shaders-" + Guid.NewGuid().ToString("N"));
    private string D3D11 => Path.Combine(_root, "D3D11");
    private string MappingPath => Path.Combine(D3D11, ShaderCacheMapping.FileName);

    public ShaderCacheMappingTests() => Directory.CreateDirectory(D3D11);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>The file v1.4.8.119303 wrote after its prompt for Native, SandBoxCore, Sandbox, CustomBattle and StoryMode.</summary>
    private static readonly byte[] Vanilla = Convert.FromHexString(
        "83070000" + "06000000" + "313139333033" + "31000000" +
        "437573746F6D426174746C653B4E61746976653B53616E64426F78436F72653B53616E64626F783B53746F72794D6F6465");

    private static readonly string[] VanillaIds = ["Native", "SandBoxCore", "Sandbox", "CustomBattle", "StoryMode"];

    [Fact]
    public void Reads_and_writes_the_games_own_file_byte_for_byte()
    {
        var mapping = ShaderCacheMapping.Parse(Vanilla);
        Assert.Equal(new ShaderCacheMapping.Mapping(0x783, "119303", "CustomBattle;Native;SandBoxCore;Sandbox;StoryMode"), mapping);
        Assert.Equal(Vanilla, ShaderCacheMapping.Serialize(mapping!));
    }

    [Fact]
    public void Sorts_ids_ordinally_as_the_engine_does()
    {
        // Ordinal puts "SandBoxCore" before "Sandbox" ('B' < 'b'); a culture-aware sort would not.
        Assert.Equal("CustomBattle;Native;SandBoxCore;Sandbox;StoryMode", ShaderCacheMapping.ModulesKey(VanillaIds));
    }

    [Theory]
    [InlineData("")]
    [InlineData("83070000")]
    [InlineData("8307000006000000313139")]
    [InlineData("830700000600000031313933303301000000417F")]
    public void Refuses_anything_that_is_not_the_known_layout(string hex)
    {
        Assert.Null(ShaderCacheMapping.Parse(Convert.FromHexString(hex)));
    }

    [Fact]
    public void A_new_mod_set_clears_the_cache_and_records_the_set()
    {
        File.WriteAllBytes(MappingPath, Vanilla);
        File.WriteAllText(Path.Combine(D3D11, "12v.sacx"), "compiled");

        var note = ShaderCacheMapping.Prepare([.. VanillaIds, "ModderLords.CompatSync"], _root);

        Assert.NotNull(note);
        Assert.Equal([MappingPath], Directory.GetFiles(D3D11));
        var written = ShaderCacheMapping.Parse(File.ReadAllBytes(MappingPath))!;
        Assert.Equal(0x783u, written.Format);
        Assert.Equal("119303", written.Build);
        Assert.Equal("CustomBattle;ModderLords.CompatSync;Native;SandBoxCore;Sandbox;StoryMode", written.Modules);
    }

    [Fact]
    public void The_same_mod_set_in_another_order_changes_nothing()
    {
        File.WriteAllBytes(MappingPath, Vanilla);
        var cached = Path.Combine(D3D11, "12v.sacx");
        File.WriteAllText(cached, "compiled");

        Assert.Null(ShaderCacheMapping.Prepare(VanillaIds.Reverse(), _root));
        Assert.True(File.Exists(cached));
        Assert.Equal(Vanilla, File.ReadAllBytes(MappingPath));
    }

    [Fact]
    public void Leaves_an_unknown_file_and_no_file_to_the_game()
    {
        Assert.Null(ShaderCacheMapping.Prepare(VanillaIds, _root));
        Assert.Null(ShaderCacheMapping.Prepare(VanillaIds, Path.Combine(_root, "missing")));

        File.WriteAllBytes(MappingPath, [1, 2, 3]);
        var cached = Path.Combine(D3D11, "12v.sacx");
        File.WriteAllText(cached, "compiled");
        Assert.Contains("not in a layout", ShaderCacheMapping.Prepare(VanillaIds, _root));
        Assert.True(File.Exists(cached));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(MappingPath));
    }
}
