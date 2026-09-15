using ModderLords.App;
using ModderLords.Core.Compat.Authority;
using Xunit;

namespace ModderLords.App.Tests;

public sealed class AuthorityRowTests
{
    private static RootVerdict V(string method, RootTrigger trigger, AuthorityVerdict verdict, string reason = "why",
        IReadOnlyList<string>? evidence = null, PatchInfo? patch = null, bool opaque = false) =>
        new(new AuthorityRoot(method, trigger, "DailyTickEvent", patch), verdict, reason, evidence ?? [], opaque);

    private static AuthorityReport Report() => new()
    {
        ModuleId = "TAOM",
        Roots =
        [
            V("TAOM.Features.Refuge.Hooks.RefugeCampaignBehavior::OnTick", RootTrigger.Simulation, AuthorityVerdict.Local),
            V("TAOM.Features.FieldCamp.Hooks.FieldCampCampaignBehavior::OnHourlyTick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly,
                "simulation calls DestroyPartyAction.Apply, which Coop blocks on clients",
                ["FieldCampCampaignBehavior.OnHourlyTick", "calls DestroyPartyAction.Apply, which Coop blocks on clients"]),
            V("TAOM.Features.Diplomacy.Hooks.AllianceCampaignBehavior_StartAlliance_Patch::Postfix", RootTrigger.Patch, AuthorityVerdict.LeakingPostfix,
                patch: new PatchInfo("TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior", "StartAlliance", PatchKind.Postfix, false)),
            V("TAOM.X+<>c::<RegisterEvents>b__3_0", RootTrigger.Session, AuthorityVerdict.Both, opaque: true),
        ],
    };

    [Fact]
    public void Rows_are_ordered_action_first_and_flag_what_needs_attention()
    {
        var rows = AuthorityRow.From(Report());
        Assert.Equal(["ServerOnly", "LeakingPostfix", "Both", "Local"], rows.Select(r => r.Verdict));
        Assert.Equal([true, true, false, false], rows.Select(r => r.ActionNeeded));
    }

    [Fact]
    public void Entry_points_are_shortened_but_the_full_method_is_kept()
    {
        var row = AuthorityRow.From(Report()).Single(r => r.Verdict == "ServerOnly");
        Assert.Equal("FieldCampCampaignBehavior.OnHourlyTick", row.Entry);
        Assert.Equal("TAOM.Features.FieldCamp.Hooks.FieldCampCampaignBehavior::OnHourlyTick", row.Method);
        Assert.Equal("X+<>c.<RegisterEvents>b__3_0", AuthorityRow.ShortName("TAOM.X+<>c::<RegisterEvents>b__3_0"));
        Assert.Equal("NoSeparator", AuthorityRow.ShortName("NoSeparator"));
    }

    [Fact]
    public void Details_carry_the_call_path_patch_target_and_reflection_note()
    {
        var rows = AuthorityRow.From(Report());
        Assert.Contains("via FieldCampCampaignBehavior.OnHourlyTick -> calls DestroyPartyAction.Apply", rows.Single(r => r.Verdict == "ServerOnly").Details);
        Assert.Contains("patches TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior.StartAlliance (Postfix)", rows.Single(r => r.Verdict == "LeakingPostfix").Details);
        Assert.Contains("uses reflection", rows.Single(r => r.Verdict == "Both").Details);
    }

    [Fact]
    public void Details_list_flags_and_split_gates_and_player_settings_need_action()
    {
        var report = new AuthorityReport
        {
            ModuleId = "IG",
            Roots =
            [
                V("IG.GarrisonPartyBehavior::OnGameOpen", RootTrigger.Session, AuthorityVerdict.NeedsStateSync) with
                {
                    Flags = ["RegistersUI: registers CampaignGameStarter.AddGameMenuOption in MainMenu..ctor"],
                    GateInstead = ["IG.GarrisonPartyBehavior::OnGameStartSetAllIGParties"],
                },
                V("IG.RecruitmentUIVM+<>c::<Init>b__1", RootTrigger.PlayerInput, AuthorityVerdict.PlayerStateUnsynced) with
                {
                    RelayVia = "IG.RecruitmentSettings::SetRecruitmentThreshold",
                },
            ],
        };
        var rows = AuthorityRow.From(report);
        var split = rows.Single(r => r.Verdict == "NeedsStateSync");
        Assert.Contains("flag: RegistersUI", split.Details);
        Assert.Contains("gated instead on clients: GarrisonPartyBehavior.OnGameStartSetAllIGParties", split.Details);
        Assert.True(rows.Single(r => r.Verdict == "PlayerStateUnsynced").ActionNeeded);
        Assert.Contains("relayed to the server via RecruitmentSettings.SetRecruitmentThreshold", rows.Single(r => r.Verdict == "PlayerStateUnsynced").Details);
    }

    [Fact]
    public void A_report_with_no_entry_points_gives_no_rows()
    {
        Assert.Empty(AuthorityRow.From(new AuthorityReport { ModuleId = "Data", NotAnalysable = true }));
    }
}
