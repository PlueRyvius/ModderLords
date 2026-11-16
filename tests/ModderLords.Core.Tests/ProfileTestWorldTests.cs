using ModderLords.Coop.Testing;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The test world a generating profile (TAOM, or the Host tab's tick box) keeps for its Coop runs: generated once,
/// copied fresh every run, and made again when the profile's mods change.
/// </summary>
public sealed class ProfileTestWorldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-profileworld").FullName;
    private string Worlds => Path.Combine(_root, "test-worlds");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly string[] Mods = ["TAOM.Dependencies", "TAOM", "TAOM_Map", "ModderLords.Compat"];

    private string Generated(string session = "{\"Players\":[]}")
    {
        var saves = Directory.CreateDirectory(Path.Combine(_root, "saves")).FullName;
        File.WriteAllBytes(Path.Combine(saves, "mltest-profile-taom-test.sav"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(saves, "mltest-profile-taom-test.json"), session);
        return Path.Combine(saves, "mltest-profile-taom-test.sav");
    }

    [Theory]
    [InlineData("TAOM-Test", "profile-taom-test")]
    [InlineData("My Host!", "profile-my-host")]
    [InlineData("!!", "profile-unnamed")]
    public void Each_profile_has_one_named_test_world(string profile, string expected) =>
        Assert.Equal(expected, ProfileTestWorld.Name(profile));

    [Fact]
    public void A_profile_with_no_kept_world_generates_one()
    {
        Assert.Contains("no test world", ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods, newWorld: false));
    }

    [Fact]
    public void A_kept_world_is_reused_while_the_mods_are_the_same()
    {
        var kept = ProfileTestWorld.Keep(Generated(), Worlds, "profile-taom-test", Mods);

        Assert.Equal(Path.Combine(Worlds, "profile-taom-test.sav"), kept);
        Assert.True(File.Exists(Path.Combine(Worlds, "profile-taom-test.json")));   // Coop's session file travels with it
        Assert.Null(ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods, newWorld: false));
        Assert.Null(ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods.Select(m => m.ToLowerInvariant()).ToList(), newWorld: false));
    }

    [Fact]
    public void Changed_mods_a_new_load_order_or_new_world_make_a_new_one()
    {
        ProfileTestWorld.Keep(Generated(), Worlds, "profile-taom-test", Mods);

        Assert.Contains("mods changed", ProfileTestWorld.Stale(Worlds, "profile-taom-test", [.. Mods, "SomeMod"], newWorld: false));
        Assert.Contains("mods changed", ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods.Reverse().ToList(), newWorld: false));
        Assert.Contains("--new-world", ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods, newWorld: true));
    }

    [Fact]
    public void A_kept_world_without_its_note_is_made_again()
    {
        ProfileTestWorld.Keep(Generated(), Worlds, "profile-taom-test", Mods);
        File.Delete(ProfileTestWorld.NotePath(Worlds, "profile-taom-test"));

        Assert.Contains("no note", ProfileTestWorld.Stale(Worlds, "profile-taom-test", Mods, newWorld: false));
    }
}
