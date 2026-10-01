using ModderLords.CompatSync;
using ModderLords.Core.Compat;
using ModderLords.Core.Overlay;
using ModderLords.Coop.Launch;

namespace ModderLords.Core.Tests;

public sealed class SettingsMemberPolicyTests
{
    // Fourberie's Settings shape: key bindings, one-key/two-key switches and gameplay switches on one object.
    private sealed class FakeSettings
    {
        public TaleWorlds.InputSystem.InputKey BaseMenuButton { get; set; }
        public TaleWorlds.InputSystem.InputKey? OptionalButton { get; set; }
        public bool BaseMenuOneButton { get; set; }
        public bool MinorFactionRecruitment { get; set; }
    }

    private static readonly string Type = typeof(FakeSettings).FullName!;

    [Fact]
    public void KeyBindingsAreAlwaysEachPlayersOwn()
    {
        Assert.True(SettingsMemberPolicy.IsPersonal(typeof(FakeSettings), "BaseMenuButton", typeof(TaleWorlds.InputSystem.InputKey), []));
        Assert.True(SettingsMemberPolicy.IsPersonal(typeof(FakeSettings), "OptionalButton", typeof(TaleWorlds.InputSystem.InputKey?), []));
    }

    [Fact]
    public void GameplaySwitchesStillFollowTheHost() =>
        Assert.False(SettingsMemberPolicy.IsPersonal(typeof(FakeSettings), "MinorFactionRecruitment", typeof(bool), [Type + "::BaseMenuOneButton"]));

    [Fact]
    public void ACompatRecordCanKeepOneMemberLocal()
    {
        Assert.True(SettingsMemberPolicy.IsPersonal(typeof(FakeSettings), "BaseMenuOneButton", typeof(bool), [Type + "::BaseMenuOneButton"]));
        // A whole-type entry is the type rule's business, not a member match.
        Assert.False(SettingsMemberPolicy.IsPersonal(typeof(FakeSettings), "BaseMenuOneButton", typeof(bool), [Type]));
    }
}

public sealed class FourberieCompatRecordTests
{
    [Fact]
    public void TheBundledRecordRunsFourberieAndKeepsHotkeyChoicesLocal()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "compat-db.json");
        if (!File.Exists(bundled)) bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ModderLords.Core", "compat-db.json"));
        var db = CompatDb.Load(bundled, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        var record = db.Find("Fourberie");
        Assert.NotNull(record);
        Assert.Equal(ServerRole.Run, record!.DefaultRole);
        foreach (var member in new[] { "BaseMenuOneButton", "HordeMenuOneButton", "TacticsMenuOneButton", "LootCorpsesOneButton" })
            Assert.Contains("Fourberie.Settings::" + member, record.IgnoreSettingsTypes);
        Assert.DoesNotContain("Fourberie.Settings", record.IgnoreSettingsTypes);
    }
}

public sealed class FourberieLaunchPolicyTests
{
    [Fact]
    public void FourberieWithoutSettingsSyncIsRefused() =>
        Assert.Contains("Settings sync", FourberieLaunchPolicy.SyncProblem(["Native", "Fourberie"], settingsSync: false));

    [Fact]
    public void FourberieWithSettingsSyncIsAllowed() =>
        Assert.Null(FourberieLaunchPolicy.SyncProblem(["Native", "fourberie"], settingsSync: true));

    [Fact]
    public void OtherProfilesAreUnchanged() =>
        Assert.Null(FourberieLaunchPolicy.SyncProblem(["Native", "ImprovedGarrisons"], settingsSync: false));

    [Fact]
    public void ServerOnlyLogicOnFourberieIsWarnedAbout()
    {
        Assert.NotNull(FourberieLaunchPolicy.ServerOnlyLogicProblem(["Fourberie"]));
        Assert.Null(FourberieLaunchPolicy.ServerOnlyLogicProblem(["ImprovedGarrisons"]));
    }
}
