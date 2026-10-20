using ModderLords.Coop.Config;

namespace ModderLords.Core.Tests;

public sealed class DifficultyPresetTests
{
    [Fact]
    public void Bannerlord_IsRealisticThroughout_ButStealthStaysEasy()
    {
        var values = DifficultyPresets.ValuesFor("Bannerlord").ToDictionary(v => v.Path, v => v.Value);
        Assert.Equal("Easy", values["difficulty.stealthAndDisguiseDifficulty"]);
        Assert.All(values.Where(v => v.Key != "difficulty.stealthAndDisguiseDifficulty"), v => Assert.Equal("Realistic", v.Value));
    }

    [Fact]
    public void Presets_OnlyTouchDifficultyLevels()
    {
        foreach (var name in DifficultyPresets.Names)
            Assert.All(DifficultyPresets.ValuesFor(name), v =>
            {
                Assert.StartsWith("difficulty.", v.Path);
                Assert.Contains(v.Value, ModConfig.Choices[v.Path]);   // every value is one the Gameplay tab offers
            });
        Assert.DoesNotContain(DifficultyPresets.Settings, s => s.Path is "difficulty.birthAndDeath" or "difficulty.autoAllocateClanMemberPerks");
        Assert.Empty(DifficultyPresets.ValuesFor(DifficultyPresets.Custom));
    }

    [Fact]
    public void Detect_FindsThePreset_OrCustom()
    {
        var file = DifficultyPresets.ValuesFor("Warrior").ToDictionary(v => v.Path, v => (string?)v.Value);
        file["modOptions.battleSize"] = "300";   // Coop's own options never decide the preset
        string? ValueOf(string path) => file.TryGetValue(path, out var v) ? v : null;
        Assert.Equal("Warrior", DifficultyPresets.Detect(ValueOf));

        file["difficulty.battleDeath"] = "Realistic";
        Assert.Equal(DifficultyPresets.Custom, DifficultyPresets.Detect(ValueOf));

        file.Remove("difficulty.battleDeath");   // a file without a preset's key matches no preset
        Assert.Equal(DifficultyPresets.Custom, DifficultyPresets.Detect(ValueOf));
    }

    [Fact]
    public void Freebooter_IsANewVanillaCampaign()
    {
        // The game starts a campaign with every difficulty at Very easy, which is what Coop's shipped template holds.
        Assert.All(DifficultyPresets.ValuesFor("Freebooter"), v => Assert.Equal("VeryEasy", v.Value));
    }
}
