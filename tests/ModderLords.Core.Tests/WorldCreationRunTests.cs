using ModderLords.Core.Profiles;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Testing;

namespace ModderLords.Core.Tests;

/// <summary>
/// The decision the Host tab and the test harness's Coop runs share: whether a launch must generate its world first.
/// </summary>
public sealed class WorldCreationRunTests
{
    private static Profile With(string? saveName, bool generate, params string[] ids) => new()
    {
        SaveName = saveName ?? "",
        GenerateWorldWithActiveMods = generate,
        Mods = ids.Select(id => new ProfileMod { Id = id, Enabled = true }).ToList(),
    };

    private static readonly string[] Taom = ["TAOM.Dependencies", "TAOM", "TAOM_Map", "LOTRLOME_Armory"];

    [Fact]
    public void A_TAOM_profile_whose_save_is_missing_generates_it_under_its_own_name()
    {
        var p = With("mltest-taom", false, Taom);
        Assert.True(WorldCreationRun.Needed(p, _ => false, _ => "unused", _ => { }));
        Assert.Equal("mltest-taom", p.SaveName);
    }

    [Fact]
    public void A_save_that_exists_is_loaded_and_never_generated_over()
    {
        Assert.False(WorldCreationRun.Needed(With("taom_1", false, Taom), _ => true, _ => "unused", _ => { }));
        Assert.False(WorldCreationRun.Needed(With("world_1", true, "SomeMod"), _ => true, _ => "unused", _ => { }));
    }

    [Fact]
    public void With_no_save_named_it_takes_the_callers_name_and_loads_it_when_a_run_already_made_it()
    {
        var said = new List<string>();
        var p = With(null, false, Taom);
        Assert.True(WorldCreationRun.Needed(p, _ => false, isTaom => isTaom ? "taom_new" : "world_new", said.Add));
        Assert.Equal("taom_new", p.SaveName);
        Assert.Contains(said, s => s.Contains("creating 'taom_new'"));

        var again = With(null, false, Taom);
        Assert.False(WorldCreationRun.Needed(again, name => name == "taom_new", _ => "taom_new", said.Add));
        Assert.Equal("taom_new", again.SaveName);
    }

    [Fact]
    public void A_vanilla_profile_without_the_tick_box_never_generates()
    {
        var p = With(null, false, "SomeMod");
        Assert.False(WorldCreationRun.Needed(p, _ => false, _ => "unused", _ => { }));
        Assert.Equal("", p.SaveName);
    }

    [Fact]
    public void An_incomplete_TAOM_recipe_is_left_to_Prepare_to_explain()
    {
        Assert.False(WorldCreationRun.Needed(With("t", false, "TAOM", "TAOM_Map"), _ => false, _ => "unused", _ => { }));
    }

    [Theory]
    [InlineData("TAOM-Test", "mltest-world-taom-test")]
    [InlineData("My Host!", "mltest-world-my-host")]
    [InlineData("!!", "mltest-world-profile")]
    public void The_harness_names_one_test_world_per_profile(string profile, string expected) =>
        Assert.Equal(expected, InGameTestRun.TestWorldSaveName(profile));
}
