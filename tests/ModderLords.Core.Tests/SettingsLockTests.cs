using ModderLords.Coop.Compat;
using ModderLords.Coop.Launch;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

/// <summary>Settings sync per mod: the Mods list's "Locked to host", as the launcher hands it to the server's module.</summary>
public sealed class SettingsLockTests
{
    private static Profile ProfileWith(params (string Id, bool? Lock)[] mods) =>
        new() { Mods = mods.Select(m => new ProfileMod { Id = m.Id, LockSettingsToHost = m.Lock }).ToList() };

    [Fact]
    public void PlayersOwn_IsOnlySelectedModsExplicitlyUnlocked()
    {
        var profile = ProfileWith(("Locked", null), ("AlsoLocked", true), ("Free", false), ("FreeButOff", false));
        Assert.Equal(["Free"], SettingsLockPolicy.PlayersOwn(profile, ["Locked", "AlsoLocked", "Free"]));
    }

    [Fact]
    public void PlayersOwn_IsEmpty_ForAProfileFromBeforeTheSetting()
    {
        // An old profile has no LockSettingsToHost at all: every mod stays locked, as it always was.
        var profile = ProfileStoreRoundTrip("""{ "Mods": [ { "Id": "A" }, { "Id": "B" } ] }""");
        Assert.Empty(SettingsLockPolicy.PlayersOwn(profile, ["A", "B"]));
    }

    [Fact]
    public void Warnings_NameModsThatRelyOnTheHostsSettings()
    {
        var warnings = SettingsLockPolicy.Warnings(["Fourberie", "BetterEconomy", "Curated", "Plain"], id => id == "Curated").ToList();
        Assert.Equal(3, warnings.Count);
        Assert.StartsWith("Fourberie:", warnings[0]);
        Assert.StartsWith("BetterEconomy:", warnings[1]);
        Assert.StartsWith("Curated:", warnings[2]);
    }

    [Fact]
    public void Recipes_CarryPlayersOwnSettings_ForTheModule()
    {
        var set = RecipeSet.Build([], "test");
        Assert.DoesNotContain("PlayersOwnSettings", set.ToJson());   // nothing unlocked: not written at all
        set.PlayersOwnSettings = ["Free"];
        var json = set.ToJson();
        Assert.Equal(["Free"], RecipeSet.FromJson(json).PlayersOwnSettings);
        // The module reads it with MiniJson, and the legacy recipe check still accepts the file.
        var root = ModderLords.CompatSync.MiniJson.ParseObject(json);
        Assert.Equal(["Free"], ModderLords.CompatSync.MiniJson.GetArray(root, "PlayersOwnSettings")!.Cast<string>());
        Assert.True(ModderLords.CompatSync.LegacyRecipePolicy.Accept(json, out _));
    }

    private static Profile ProfileStoreRoundTrip(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!;
}
