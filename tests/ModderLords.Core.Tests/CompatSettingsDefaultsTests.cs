using ModderLords.Core.Compat;
using ModderLords.Coop.Live;

namespace ModderLords.Core.Tests;

/// <summary>
/// Compat-database setting defaults folded into a launch's host overrides. First user: TAOM's EnableTroopWeight, which
/// TAOM's own co-op package forces off because TroopWeight trims rosters from each peer's simulation.
/// </summary>
public class CompatSettingsDefaultsTests
{
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> TroopWeightOff =
        new Dictionary<string, Dictionary<string, string>> { ["TAOM"] = new() { ["EnableTroopWeight"] = "false" } };

    [Fact]
    public void A_default_is_staged_when_the_profile_does_not_set_it()
    {
        var (staged, added) = CompatSettingsDefaults.Merge(new SettingsOverrides(), [("TAOM", TroopWeightOff)]);

        Assert.Equal("false", staged.Get("TAOM", "EnableTroopWeight"));
        Assert.Equal(new CompatSettingsDefaults.Applied("TAOM", "TAOM", "EnableTroopWeight", "false"), Assert.Single(added));
    }

    [Fact]
    public void The_hosts_own_override_wins()
    {
        var profile = new SettingsOverrides();
        profile.Set("TAOM", new Dictionary<string, string> { ["EnableTroopWeight"] = "true" });

        var (staged, added) = CompatSettingsDefaults.Merge(profile, [("TAOM", TroopWeightOff)]);

        Assert.Equal("true", staged.Get("TAOM", "EnableTroopWeight"));
        Assert.Empty(added);
    }

    [Fact]
    public void Other_profile_overrides_are_kept_and_the_profile_is_not_modified()
    {
        var profile = new SettingsOverrides();
        profile.Set("TAOM", new Dictionary<string, string> { ["SomethingElse"] = "5" });

        var (staged, _) = CompatSettingsDefaults.Merge(profile, [("TAOM", TroopWeightOff)]);

        Assert.Equal("5", staged.Get("TAOM", "SomethingElse"));
        Assert.Equal("false", staged.Get("TAOM", "EnableTroopWeight"));
        Assert.Null(profile.Get("TAOM", "EnableTroopWeight"));
    }

    [Fact]
    public void No_defaults_stage_exactly_the_profile()
    {
        var (staged, added) = CompatSettingsDefaults.Merge(new SettingsOverrides(), []);
        Assert.True(staged.IsEmpty);
        Assert.Empty(added);
    }

    [Fact]
    public void The_bundled_taom_record_turns_troop_weight_off()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "compat-db.json");
        if (!File.Exists(bundled)) bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ModderLords.Core", "compat-db.json"));
        var db = CompatDb.Load(bundled, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        Assert.Equal("false", db.Find("TAOM")!.DefaultSettings["TAOM"]["EnableTroopWeight"]);
    }
}
