using ModderLords.Coop.Launch;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Tests;

/// <summary>
/// Server-only logic does nothing without Settings sync: the shared module is what carries the recipe to players.
/// A host and join on 2026-09-14 ran that way with only a console line to show for it, so launch now refuses.
/// </summary>
public sealed class ServerOnlyLogicPreflightTests
{
    private static Profile Profile(bool settingsSync) => new()
    {
        SettingsSync = settingsSync,
        Mods =
        [
            new ProfileMod { Id = "TAOM", ServerAuthoritative = true },
            new ProfileMod { Id = "ImprovedGarrisons", Enabled = false, ServerAuthoritative = true },
            new ProfileMod { Id = "LOTRLOME_Armory" },
        ],
    };

    [Fact]
    public void Refuses_a_ticked_mod_that_will_run_while_settings_sync_is_off()
    {
        var problem = LaunchSession.ServerOnlyLogicProblem(Profile(settingsSync: false), ["TAOM", "LOTRLOME_Armory"]);
        Assert.NotNull(problem);
        Assert.StartsWith("Server-only logic is ticked for TAOM, but Settings sync is off.", problem);
        Assert.DoesNotContain("ImprovedGarrisons", problem);   // disabled in the profile
    }

    [Fact]
    public void Quiet_when_settings_sync_is_on()
    {
        Assert.Null(LaunchSession.ServerOnlyLogicProblem(Profile(settingsSync: true), ["TAOM"]));
    }

    [Fact]
    public void Quiet_when_no_ticked_mod_is_part_of_the_launch()
    {
        Assert.Null(LaunchSession.ServerOnlyLogicProblem(Profile(settingsSync: false), ["LOTRLOME_Armory"]));
        Assert.Null(LaunchSession.ServerOnlyLogicProblem(new Profile { Mods = [new ProfileMod { Id = "TAOM" }] }, ["TAOM"]));
    }
}
