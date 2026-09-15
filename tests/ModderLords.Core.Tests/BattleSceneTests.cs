using ModderLords.CompatSync.Coop;
using ModderLords.Core.Compat;
using ModderLords.Coop.Compat;

namespace ModderLords.Core.Tests;

/// <summary>Scenes without a terrain shader sack: found by the launcher, avoided by the server's deterministic re-pick.</summary>
public sealed class BattleSceneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-scenes-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Module(string name, params (string scene, bool sack)[] scenes)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (scene, sack) in scenes)
        {
            var dir = Path.Combine(folder, "SceneObj", scene);
            Directory.CreateDirectory(Path.Combine(dir, "ShaderCache", "D3D11"));
            File.WriteAllText(Path.Combine(dir, "scene.xscene"), "");
            File.WriteAllText(Path.Combine(dir, "ShaderCache", "D3D11", "terrain_shaders_header_data.bin"), "");
            if (sack) File.WriteAllText(Path.Combine(dir, BattleSceneCache.SackRelativePath), "x");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Scan_TheLastModuleInOrderDecides()
    {
        var vanilla = Module("SandBoxCore", ("battle_terrain_001", true), ("battle_terrain_020", false), ("battle_terrain_a", false), ("town_a", true));
        var fixer = Module("ShaderFixMod", ("battle_terrain_020", true));          // ships the sack vanilla lacks
        var breaker = Module("RetextureMod", ("town_a", false));                    // overrides a good scene with a sackless copy
        var noScenes = Module("CodeOnlyMod");

        Assert.Equal(["battle_terrain_020", "battle_terrain_a"], BattleSceneCache.Scan([vanilla]));
        Assert.Equal(["battle_terrain_a", "town_a"], BattleSceneCache.Scan([vanilla, fixer, breaker, noScenes]));
        Assert.Equal(["battle_terrain_020", "battle_terrain_a"], BattleSceneCache.Scan([fixer, vanilla]));   // vanilla later wins again
        Assert.Empty(BattleSceneCache.Scan([Path.Combine(_root, "missing")]));
    }

    [Fact]
    public void Replace_KeepsAGoodPick_AndSwapsABadOneStably()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "battle_terrain_020", "battle_terrain_a" };
        IReadOnlyList<string>[] tiers = [["battle_terrain_020", "battle_terrain_d", "battle_terrain_b"], ["battle_terrain_x"]];

        Assert.Null(BattleScenePolicy.Replace("battle_terrain_d", tiers, excluded, 5));
        var a = BattleScenePolicy.Replace("battle_terrain_020", tiers, excluded, 12345);
        Assert.Contains(a, new[] { "battle_terrain_b", "battle_terrain_d" });
        Assert.Equal(a, BattleScenePolicy.Replace("battle_terrain_020", [["battle_terrain_b", "battle_terrain_020", "battle_terrain_d"]], excluded, 12345));   // order-independent
        Assert.Equal(a, BattleScenePolicy.Replace("battle_terrain_020", tiers, excluded, 12345));                                                       // stable
        // Different seeds spread over the candidates.
        var picks = Enumerable.Range(0, 64).Select(seed => BattleScenePolicy.Replace("battle_terrain_020", tiers, excluded, seed)).Distinct().Count();
        Assert.Equal(2, picks);
    }

    [Fact]
    public void Replace_FallsThroughTiers_AndKeepsTheOriginalWhenNothingIsSafe()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "battle_terrain_020", "battle_terrain_a" };
        Assert.Equal("battle_terrain_x", BattleScenePolicy.Replace("battle_terrain_020", [["battle_terrain_020", "battle_terrain_a"], ["battle_terrain_x"]], excluded, 1));
        Assert.Equal("battle_terrain_020", BattleScenePolicy.Replace("battle_terrain_020", [["battle_terrain_020"], ["battle_terrain_a"]], excluded, 1));
        Assert.Null(BattleScenePolicy.Replace("", [["battle_terrain_x"]], excluded, 1));
    }

    [Fact]
    public void Hash_IsTheSameEveryTime_AndDiffersBySeed()
    {
        Assert.Equal(BattleScenePolicy.Hash(42), BattleScenePolicy.Hash(42));
        Assert.NotEqual(BattleScenePolicy.Hash(42), BattleScenePolicy.Hash(43));
    }

    /// <summary>Real install, skipped when absent: vanilla SandBoxCore ships exactly two sackless battle scenes.</summary>
    [Fact]
    public void Probe_VanillaSandBoxCore_ShipsTwoSacklessBattleScenes()
    {
        var gameRoot = ModderLords.Core.Launch.GamePaths.FindGameRoot();
        if (gameRoot is null) return;
        var sandBoxCore = Path.Combine(gameRoot, "Modules", "SandBoxCore");
        if (!Directory.Exists(Path.Combine(sandBoxCore, "SceneObj"))) return;
        var excluded = BattleSceneCache.Scan([Path.Combine(gameRoot, "Modules", "Native"), sandBoxCore]);
        Assert.Equal(["battle_terrain_020", "battle_terrain_a"], excluded.Where(s => s.StartsWith("battle_terrain", StringComparison.Ordinal)).ToList());
        Assert.Contains("sturgia_village_g", excluded);
    }

    [Fact]
    public void Recipe_CarriesTheExclusions_OrOmitsThemWhenOff()
    {
        var on = RecipeSet.Build([], "test", excludedBattleScenes: ["battle_terrain_020"]);
        Assert.Contains("\"ExcludedBattleScenes\"", on.ToJson());
        Assert.Equal(["battle_terrain_020"], RecipeSet.FromJson(on.ToJson()).ExcludedBattleScenes);
        var off = RecipeSet.Build([], "test");
        Assert.DoesNotContain("ExcludedBattleScenes", off.ToJson());
        Assert.Null(RecipeSet.FromJson(off.ToJson()).ExcludedBattleScenes);
    }
}
