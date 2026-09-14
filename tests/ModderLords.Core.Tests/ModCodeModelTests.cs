using ModderLords.Core.Compat.Authority;

namespace ModderLords.Core.Tests;

public sealed class ModCodeModelTests
{
    private const string P = "ModderLords.Core.Tests.ModFakes.";
    private static readonly Lazy<ModCodeModel> Self = new(() => ModAnalysis.Analyse("Tests", [typeof(ModCodeModelTests).Assembly.Location]));

    private static bool HasRoot(string method, RootTrigger trigger) => Self.Value.Roots.Any(r => r.Method == method && r.Trigger == trigger);

    [Fact]
    public void CampaignListeners_SplitSimulationFromSession()
    {
        Assert.True(HasRoot(P + "ModBehavior::OnDailyTickParty", RootTrigger.Simulation));
        Assert.True(HasRoot(P + "ModBehavior::AddMenus", RootTrigger.Session));
        Assert.Contains(Self.Value.Roots, r => r.Method == P + "ModBehavior::OnDailyTickParty" && r.Detail == "DailyTickPartyEvent");
    }

    [Fact]
    public void MenuAndDialogDelegates_ConditionIsQuery_ConsequenceIsPlayerInput()
    {
        Assert.True(HasRoot(P + "ModBehavior::MenuCondition", RootTrigger.Query));
        Assert.True(HasRoot(P + "ModBehavior::MenuConsequence", RootTrigger.PlayerInput));
        // Null condition keeps its slot, so the lambda is the consequence.
        Assert.Contains(Self.Value.Roots, r => r.Trigger == RootTrigger.PlayerInput && r.Detail == "AddPlayerLine" && r.Method.Contains("<AddMenus>"));
        Assert.DoesNotContain(Self.Value.Roots, r => r.Trigger == RootTrigger.Query && r.Detail == "AddPlayerLine");
    }

    [Fact]
    public void AttributePatch_AndManualPatch_ResolveTargetAndKind()
    {
        var attr = Self.Value.Roots.Single(r => r.Method == P + "WagePatch::Tweak");
        Assert.Equal(new PatchInfo(P + "DefaultPartyWageModel", "GetCharacterWage", PatchKind.Postfix, false), attr.Patch);
        var manual = Self.Value.Roots.Single(r => r.Method == P + "ModSubModule::ManualPostfix");
        Assert.Equal(new PatchInfo(P + "DefaultPartyWageModel", "GetCharacterWage", PatchKind.Postfix, true), manual.Patch);
    }

    [Fact]
    public void TypeFamilies_ModelMissionViewModelSubModule()
    {
        Assert.True(HasRoot(P + "ModWageModel::GetCharacterWage", RootTrigger.Query));
        Assert.False(HasRoot(P + "DefaultPartyWageModel::GetCharacterWage", RootTrigger.Query));   // declares, does not override
        Assert.True(HasRoot(P + "ModMission::OnAgentRemoved", RootTrigger.Mission));
        Assert.True(HasRoot(P + "ModVM::ExecuteDone", RootTrigger.PlayerInput));
        Assert.DoesNotContain(Self.Value.Roots, r => r.Method == P + "ModVM::Refresh");
        Assert.True(HasRoot(P + "ModSubModule::OnSubModuleLoad", RootTrigger.Lifecycle));
    }

    [Fact]
    public void CallGraph_FollowsInterfaceDispatch_AndRecordsWrites()
    {
        var g = Self.Value;
        Assert.Contains(P + "IShedService::Shed", g.Methods[P + "ModBehavior::OnDailyTickParty"].Calls);
        Assert.Contains(P + "ShedService::Shed", g.Dispatch(P + "IShedService::Shed"));
        Assert.Contains(P + "Roster::AddToCounts", g.Methods[P + "ShedService::Shed"].Calls);
        Assert.Contains(P + "Roster.Count", g.Methods[P + "Roster::AddToCounts"].FieldWrites);
    }

    [Fact]
    public void Reflection_MarksMethodOpaque()
    {
        Assert.True(Self.Value.Methods[P + "ModSubModule::Reflect"].Opaque);
        Assert.False(Self.Value.Methods[P + "ModSubModule::OnSubModuleLoad"].Opaque);
    }

    [Fact]
    public void UnreadableAssembly_IsNotAnalysable()
    {
        var path = Path.Combine(Path.GetTempPath(), "ml-bad-mod-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            var m = ModAnalysis.Analyse("Broken", [path]);
            Assert.True(m.NotAnalysable);
            Assert.NotEmpty(m.Notes);
            Assert.StartsWith("not analysable", m.Summary);
        }
        finally { File.Delete(path); }
    }
}
