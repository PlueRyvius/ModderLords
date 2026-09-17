using System.Text.Json;
using ModderLords.Core.Profiles;
using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

public class SimpleCompatibilityTests
{
    [Fact]
    public void OlderProfilesDefaultToSimpleWithoutErasingLegacyChoices()
    {
        var profile = JsonSerializer.Deserialize<Profile>("""{"SettingsSync":true,"Mods":[{"Id":"Fixture","ServerAuthoritative":true}]}""")!;
        Assert.True(profile.SimpleCompatibility);
        Assert.True(profile.SettingsSync);
        Assert.True(profile.Mods[0].ServerAuthoritative);
    }

    [Fact]
    public void SimpleLaunchIgnoresOverridesAndPreservesSavedProfile()
    {
        var saved = new Profile { CompatGuards = false, SettingsSync = true, AutomaticCompatibility = false,
            UseModDistanceCache = true, ManualLoadOrder = true, SaveName = "disposable" };
        saved.Mods.Add(new() { Id = "OriginalFixture", Role = ServerRole.DependencyOnly, ServerAuthoritative = true,
            ClientSideBehaviors = ["Fixture.Ui"], SourcePath = "selected-copy", Enabled = true });
        var before = JsonSerializer.Serialize(saved);
        var simple = saved.ForServerLaunch();
        Assert.Equal(before, JsonSerializer.Serialize(saved));
        Assert.True(simple.CompatGuards);
        Assert.False(simple.SettingsSync);
        Assert.True(simple.AutomaticCompatibility);
        Assert.False(simple.UseModDistanceCache);
        Assert.False(simple.Mods[0].ServerAuthoritative);
        Assert.Empty(simple.Mods[0].ClientSideBehaviors);
        Assert.True(simple.ManualLoadOrder);
        Assert.Equal("selected-copy", simple.Mods[0].SourcePath);
        Assert.Equal("disposable", simple.SaveName);
        Assert.True(simple.Mods[0].Enabled);
        saved.SimpleCompatibility = false;
        var advanced = saved.ForServerLaunch();
        Assert.True(advanced.SettingsSync);
        Assert.True(advanced.Mods[0].ServerAuthoritative);
        Assert.Equal(ServerRole.DependencyOnly, advanced.Mods[0].Role);
        Assert.Equal(["Fixture.Ui"], advanced.Mods[0].ClientSideBehaviors);
        simple.Mods[0].Enabled = false;
        Assert.True(saved.Mods[0].Enabled);
    }

    [Fact]
    public void ModeSurvivesProfileSnapshot()
    {
        var profile = new Profile { SimpleCompatibility = false };
        Assert.False(ProfileStore.Snapshot(profile).SimpleCompatibility);
        profile.SimpleCompatibility = true;
        Assert.True(ProfileStore.Snapshot(profile).SimpleCompatibility);
    }
}
