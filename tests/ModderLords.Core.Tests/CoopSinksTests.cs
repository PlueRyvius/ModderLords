using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Launch;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

public sealed class CoopSinksTests
{
    private const string P = "ModderLords.Core.Tests.CoopFakes.";
    private static readonly Lazy<CoopSinkCatalogue> Self = new(() => CoopSinks.ScanDll(typeof(CoopSinksTests).Assembly.Location));

    [Fact]
    public void RegisterEventsGates_BothShapes()
    {
        var c = Self.Value;
        Assert.True(c.IsBehaviourGated(P + "VanillaUpgrader"));
        Assert.True(c.IsBehaviourGated(P + "VanillaRecruitment"));
        Assert.Equal(CoopGateKind.ClientSkip, c.GateFor(P + "VanillaUpgrader", "RegisterEvents")!.Kind);
        Assert.Equal(CoopGateKind.Conditional, c.GateFor(P + "VanillaRecruitment", "RegisterEvents")!.Kind);
        Assert.Contains(P + "VanillaUpgrader", c.GatedBehaviourTypes);
    }

    [Fact]
    public void MethodGate_FromClassLevelTarget_AndConventionalPrefixName()
    {
        var c = Self.Value;
        Assert.True(c.IsBlocked(P + "VanillaGold", "ApplyInternal"));
        Assert.False(c.IsBehaviourGated(P + "VanillaGold"));
        Assert.Equal(P + "GateGold.Prefix", c.GateFor(P + "VanillaGold", "ApplyInternal")!.PatchMethod);
    }

    [Fact]
    public void PrefixThatNeverConsultsAuthority_IsNotAGate()
    {
        Assert.False(Self.Value.IsBlocked(P + "VanillaArena", "game_menu_arena"));
    }

    [Fact]
    public void SetterViaMethodType_IsPolicyGate()
    {
        Assert.Equal(CoopGateKind.Policy, Self.Value.GateFor(P + "VanillaGold", "set_Gold")!.Kind);
    }

    [Fact]
    public void ConditionalGates_SplitByWhatTheClientBranchDoes()
    {
        var c = Self.Value;
        Assert.Equal(CoopGateKind.Publishes, c.GateFor(P + "VanillaLeave", "ApplyForParty")!.Kind);
        Assert.Equal(CoopGateKind.ClientDeny, c.GateFor(P + "VanillaCheats", "CheckCheatUsage")!.Kind);
        Assert.Equal(CoopGateKind.ClientLocal, c.GateFor(P + "VanillaRoster", "AddToCounts")!.Kind);
    }

    [Fact]
    public void SyncedAndInterceptedMembers_AndTargetMethods()
    {
        var c = Self.Value;
        Assert.Contains(P + "VanillaGold._gold", c.SyncedMembers);
        Assert.Contains(P + "VanillaGold.Gold", c.SyncedMembers);
        Assert.Contains(P + "VanillaUpgrader._volunteers", c.InterceptedMembers);          // iterator transpiler body
        Assert.Contains(P + "VanillaUpgrader.UpgradeReadyTroops", c.TargetMethodsTargets); // iterator TargetMethods body
        Assert.DoesNotContain(P + "VanillaUpgrader.UpgradeReadyTroops", c.SyncedMembers);
    }

    [Fact]
    public void Load_WritesCache_AndReusesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-coopsinks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dll = typeof(CoopSinksTests).Assembly.Location;
            var first = CoopSinks.Load(dll, dir);
            Assert.Single(Directory.GetFiles(dir, "coop-sinks-*.json"));
            Assert.Equal(64, first.SourceSha256.Length);
            var second = CoopSinks.Load(dll, dir);
            Assert.Equal(first.Gates, second.Gates);
            Assert.Equal(first.SyncedMembers, second.SyncedMembers);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void MalformedAssembly_ReportsNote_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "ml-bad-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            var c = CoopSinks.ScanDll(path);
            Assert.Empty(c.Gates);
            Assert.NotEmpty(c.Notes);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Diagnostic against the real Coop install when present (skipped silently on machines without it).</summary>
public sealed class CoopSinksProbe
{
    private readonly ITestOutputHelper _out;
    public CoopSinksProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Scan_installed_coop()
    {
        var dll = CoopSinks.FindGameInterface(GamePaths.SteamLibraries());
        if (dll is null) { _out.WriteLine("no Coop install; skipped"); return; }
        var c = CoopSinks.ScanDll(dll);
        _out.WriteLine(dll);
        _out.WriteLine(c.Summary);
        foreach (var n in c.Notes.Take(10)) _out.WriteLine("note: " + n);
        foreach (var t in c.GatedBehaviourTypes) _out.WriteLine("gated: " + t);

        Assert.True(c.GatedBehaviourTypes.Count >= 46, $"only {c.GatedBehaviourTypes.Count} gated behaviours");
        Assert.True(c.IsBehaviourGated("TaleWorlds.CampaignSystem.CampaignBehaviors.PartyUpgraderCampaignBehavior"));
        Assert.True(c.IsBehaviourGated("TaleWorlds.CampaignSystem.CampaignBehaviors.RecruitmentCampaignBehavior"));
        Assert.True(c.IsBlocked("TaleWorlds.CampaignSystem.Actions.GiveGoldAction", "ApplyInternal"));
        Assert.Contains("TaleWorlds.CampaignSystem.Hero.VolunteerTypes", c.InterceptedMembers);
        Assert.Equal(CoopGateKind.Publishes, c.GateFor("TaleWorlds.CampaignSystem.Actions.LeaveSettlementAction", "ApplyForParty")!.Kind);
        Assert.Equal(CoopGateKind.ClientDeny, c.GateFor("TaleWorlds.CampaignSystem.CampaignCheats", "CheckCheatUsage")!.Kind);
        Assert.Equal(CoopGateKind.ClientLocal, c.GateFor("TaleWorlds.CampaignSystem.Roster.ItemRoster", "AddToCounts")!.Kind);
        foreach (var k in Enum.GetValues<CoopGateKind>()) _out.WriteLine($"{k}: {c.Gates.Count(g => g.Kind == k)}");
        Assert.NotEmpty(c.SyncedMembers);
    }
}
