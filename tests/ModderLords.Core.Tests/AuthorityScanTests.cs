using ModderLords.Core.Compat.Authority;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

public sealed class AuthorityScanTests
{
    private const string P = "ModderLords.Core.Tests.AuthFakes.";
    private const string W = "TaleWorlds.CampaignSystem.";

    private static readonly CoopSinkCatalogue FakeCoop = new()
    {
        Gates =
        [
            new CoopGate(W + "AuthGoldAction", "ApplyInternal", CoopGateKind.ClientSkip, "fake"),
            new CoopGate(W + "AuthVanillaBehavior", "RegisterEvents", CoopGateKind.ClientSkip, "fake"),
            new CoopGate(W + "AuthVanillaAlliance", "StartAlliance", CoopGateKind.Conditional, "fake"),
            new CoopGate(W + "AuthLeaveAction", "ApplyForParty", CoopGateKind.Publishes, "fake"),
            new CoopGate(W + "AuthCheats", "CheckCheatUsage", CoopGateKind.ClientDeny, "fake"),
            new CoopGate("TaleWorlds.Core.GameStateManager", "PushState", CoopGateKind.Conditional, "fake"),
        ],
        SyncedMembers = [W + "AuthHero.Gold"],
    };

    // Plumbing threshold 2: AuthLog.Dropped (3 writers) is plumbing, AuthBehavior.Momentum (1 writer) is shared state.
    private static readonly Lazy<(ModCodeModel model, AuthorityReport report)> Self = new(() =>
    {
        var model = ModAnalysis.Analyse("Tests", [typeof(AuthorityScanTests).Assembly.Location]);
        return (model, AuthorityScan.Classify(model, FakeCoop, plumbingWriters: 2));
    });

    [Fact]
    public void PlumbingThreshold_DecidesWhetherManyWriterStateCountsAsShared()
    {
        Assert.DoesNotContain("AuthLog", For("AuthBehavior::TickGold").Reason);
        // With the default threshold (at least 5) three writers are few enough to count as shared state.
        var lenient = AuthorityScan.Classify(Self.Value.model, FakeCoop);
        var gold = lenient.Roots.Single(r => r.Root.Method == P + "AuthBehavior::TickGold");
        Assert.Equal(AuthorityVerdict.NeedsStateSync, gold.Verdict);
        Assert.Contains("AuthLog.Dropped", gold.Reason);
    }

    private static RootVerdict For(string method) => Self.Value.report.Roots.Single(r => r.Root.Method == P + method);

    [Theory]
    [InlineData("AuthBehavior::TickGold", AuthorityVerdict.ServerOnly)]      // Apply* on an action class Coop gates
    [InlineData("AuthBehavior::TickSynced", AuthorityVerdict.ServerOnly)]    // writes a member Coop syncs
    [InlineData("AuthBehavior::TickRandom", AuthorityVerdict.ServerOnly)]    // MBRandom in simulation
    [InlineData("AuthBehavior::TickGuarded", AuthorityVerdict.AlreadyHandled)]
    [InlineData("AuthBehavior::TickMessage", AuthorityVerdict.Local)]
    [InlineData("AuthBehavior::TickMomentum", AuthorityVerdict.NeedsStateSync)] // mod state a menu condition reads
    [InlineData("AuthBehavior::Menus", AuthorityVerdict.Local)]              // registering a consequence is not running it
    [InlineData("AuthBehavior::ShowMomentum", AuthorityVerdict.Both)]
    [InlineData("AuthBehavior::BuyRenown", AuthorityVerdict.NeedsRelay)]
    [InlineData("AuthBehavior::SayHi", AuthorityVerdict.Local)]
    [InlineData("AuthUpgradePatch::Shed", AuthorityVerdict.AlreadyHandled)]   // target's behaviour is server-only
    [InlineData("AuthAlliancePatch::After", AuthorityVerdict.LeakingPostfix)]
    [InlineData("AuthWagePatch::Wage", AuthorityVerdict.Both)]
    [InlineData("AuthRecruitPatch::Swap", AuthorityVerdict.NeedsRelay)]      // patch on a menu consequence
    [InlineData("AuthMissionA::OnAgentRemoved", AuthorityVerdict.Both)]      // base call does not dispatch to a sibling
    [InlineData("AuthMissionB::OnAgentRemoved", AuthorityVerdict.Review)]
    [InlineData("AuthCareerPatch::Open", AuthorityVerdict.NeedsRelay)]       // ViewModel command = player input
    [InlineData("AuthSplitBehavior::Split", AuthorityVerdict.ServerOnly)]    // registers UI; split, still gated by parts
    [InlineData("AuthSplitBehavior::Mixed", AuthorityVerdict.Review)]        // UI and server work in one method
    [InlineData("AuthSplitBehavior::NonVoid", AuthorityVerdict.Review)]      // server work in a callee that returns a value
    [InlineData("AuthSplitBehavior::Shared", AuthorityVerdict.Review)]       // server work a menu consequence also uses
    [InlineData("AuthSplitBehavior::OpenSettings", AuthorityVerdict.Local)]  // opening a screen is navigation
    [InlineData("AuthSplitBehavior::LeaveTown", AuthorityVerdict.AlreadyHandled)] // Coop publishes its own request
    [InlineData("AuthSplitBehavior::Cheat", AuthorityVerdict.Local)]         // Coop refuses cheats on clients on purpose
    [InlineData("AuthSplitBehavior::OpenQuests", AuthorityVerdict.Review)]   // a screen Coop never opens
    [InlineData("AuthSettingsVM::Confirm", AuthorityVerdict.NeedsRelay)]     // popup callback changing the world
    [InlineData("AuthSplitBehavior::Bye", AuthorityVerdict.Local)]           // leaving the conversation is navigation
    [InlineData("AuthSplitBehavior::TickOffer", AuthorityVerdict.ServerOnly)] // a popup in a tick doesn't stop gating
    public void Verdicts(string method, AuthorityVerdict expected)
    {
        var v = For(method);
        Assert.True(expected == v.Verdict, $"{method}: expected {expected}, got {v.Verdict} ({v.Reason})");
    }

    [Fact]
    public void Evidence_EndsInTheDecidingEffect()
    {
        var gold = For("AuthBehavior::TickGold");
        Assert.Equal("AuthBehavior.TickGold", gold.Evidence[0]);
        Assert.Contains("AuthGoldAction.ApplyBetweenCharacters", gold.Evidence[^1]);
        Assert.Contains("which Coop blocks on clients", gold.Reason);
    }

    [Fact]
    public void UiRegisteringHandler_GatesOnlyItsServerWork()
    {
        var split = For("AuthSplitBehavior::Split");
        Assert.Equal([P + "AuthSplitBehavior::SetupParties"], split.GateInstead);
        Assert.Contains(split.Flags!, f => f.StartsWith("RegistersUI", StringComparison.Ordinal));
        Assert.Null(For("AuthSplitBehavior::TickSettings").GateInstead);
        Assert.Contains("returns a value", For("AuthSplitBehavior::NonVoid").Reason);
        Assert.Contains("player-facing", For("AuthSplitBehavior::Shared").Reason);
    }

    [Fact]
    public void SliderCallback_IsARoot_AndItsSettingNeverReachesTheServer()
    {
        var root = Self.Value.report.Roots.Single(r => r.Root.Detail == "UI callback" && r.Root.Method.StartsWith(P + "AuthSettingsVM", StringComparison.Ordinal));
        Assert.Equal(AuthorityVerdict.PlayerStateUnsynced, root.Verdict);
        Assert.Contains("AuthTownLimits.Max", root.Reason);   // constructor defaults don't make it plumbing
        Assert.Contains(Self.Value.report.ActionNeeded, r => r == root);
        Assert.Equal("popup callback", For("AuthSettingsVM::Confirm").Root.Detail);
    }

    [Fact]
    public void ServerRunOwnershipCheck_IsListedForTheServerRewrite()
    {
        var methods = Self.Value.report.PlayerComparisonMethods;
        Assert.Contains(P + "AuthSplitBehavior::TickOwner", methods);
        Assert.DoesNotContain(P + "AuthSplitBehavior::TickHost", methods);   // a null check, not a comparison
    }

    [Fact]
    public void TickReadingMainHero_IsFlagged()
    {
        Assert.Contains(For("AuthSplitBehavior::TickOffer").Flags!, f => f.StartsWith("PopupLost", StringComparison.Ordinal));
        Assert.Null(For("AuthSplitBehavior::TickOffer").GateInstead);
        Assert.Contains(For("AuthSplitBehavior::TickHost").Flags!, f => f.StartsWith("HostIsOnlyPlayer: reads Hero.MainHero", StringComparison.Ordinal));
    }

    [Fact]
    public void ArrayElementWrite_CountsAsFieldWrite()
    {
        Assert.Contains(P + "AuthBehavior.Slots", Self.Value.model.Methods[P + "AuthBehavior::FillSlots"].FieldWrites);
    }

    [Fact]
    public void Report_SummaryJsonAndActionNeeded()
    {
        var r = Self.Value.report;
        Assert.Contains("NeedsRelay", r.Summary);
        Assert.Contains(r.ActionNeeded, x => x.Root.Method == P + "AuthAlliancePatch::After");
        Assert.DoesNotContain(r.ActionNeeded, x => x.Verdict is AuthorityVerdict.Local or AuthorityVerdict.Both or AuthorityVerdict.AlreadyHandled);
        Assert.Contains("\"LeakingPostfix\"", r.ToJson());
    }

    [Fact]
    public void NotAnalysableModel_GivesNotAnalysableReport()
    {
        var path = Path.Combine(Path.GetTempPath(), "ml-bad-auth-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3]);
        try
        {
            var report = AuthorityScan.Classify(ModAnalysis.Analyse("Broken", [path]), new CoopSinkCatalogue());
            Assert.True(report.NotAnalysable);
            Assert.Empty(report.Roots);
            Assert.StartsWith("not analysable", report.Summary);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Golden answers against locally installed mods and Coop; each check runs only when its mod is present.</summary>
public sealed class AuthorityProbe
{
    private readonly ITestOutputHelper _out;
    public AuthorityProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Classify_installed_mods()
    {
        var mods = InstalledMods.Find();
        var gi = CoopSinks.FindGameInterface(ModderLords.Core.Launch.GamePaths.SteamLibraries());
        if (mods is null || gi is null) { _out.WriteLine("no game or Coop; skipped"); return; }
        var coop = CoopSinks.ScanDll(gi);

        var reports = new Dictionary<string, AuthorityReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "TAOM", "MyLittleWarband", "KingdomPlus", "ImprovedGarrisons", "HealOnKill", "RTSCamera" })
        {
            if (!mods.TryGetValue(id, out var mod)) continue;
            var report = AuthorityScan.Classify(ModAnalysis.Analyse(mod), coop);
            reports[id] = report;
            _out.WriteLine($"{id}: {report.Summary}");
            foreach (var r in report.ActionNeeded.OrderBy(r => r.Verdict).Take(12))
                _out.WriteLine($"  {r.Verdict,-15} {r.Root.Method}  — {r.Reason}");
        }

        RootVerdict? Find(AuthorityReport r, string method) => r.Roots.FirstOrDefault(x => x.Root.Method == method);

        if (reports.TryGetValue("TAOM", out var taom))
        {
            foreach (var r in taom.Roots.Where(r => r.Root.Method.Contains("CultureConversionBehavior", StringComparison.Ordinal)))
                _out.WriteLine($"  culture: {r.Root.Trigger} {r.Root.Method} [{r.Root.Detail}] -> {r.Verdict} ({r.Reason})");
            Assert.Equal(AuthorityVerdict.AlreadyHandled, Find(taom, "TAOM.Features.TroopWeight.Hooks.PartyUpgraderUpgradeReadyTroops_Patch::Postfix")!.Verdict);
            Assert.Contains(taom.Roots, r => r.Root.Method.StartsWith("TAOM.Features.CultureConversion.Hooks.CultureConversionBehavior::", StringComparison.Ordinal)
                && r.Verdict == AuthorityVerdict.AlreadyHandled);
            const string alliance = "TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior";
            foreach (var r in taom.Roots.Where(r => r.Root.Patch?.TargetType == alliance || r.Verdict == AuthorityVerdict.LeakingPostfix))
                _out.WriteLine($"  alliance/leak: {r.Root.Method} {r.Root.Patch} -> {r.Verdict} ({r.Reason}) via {string.Join(" -> ", r.Evidence)}");
            if (coop.IsBlocked(alliance, "StartAlliance"))
                Assert.Contains(taom.Roots, r => r.Root.Patch is { TargetType: alliance, TargetMethod: "StartAlliance", Kind: PatchKind.Postfix }
                    && r.Verdict == AuthorityVerdict.LeakingPostfix);
        }
        if (reports.TryGetValue("MyLittleWarband", out var mlw))
        {
            foreach (var r in mlw.Roots.Where(r => r.Root.Method.Contains("Recruit", StringComparison.Ordinal) || r.Root.Method.Contains("Wage", StringComparison.Ordinal)))
                _out.WriteLine($"  mlw: {r.Root.Method} -> {r.Verdict} ({r.Reason}) via {string.Join(" -> ", r.Evidence)}");
            Assert.Equal(AuthorityVerdict.AlreadyHandled, Find(mlw, "MyLittleWarband.RecruitProductionPatch::Postfix")!.Verdict);
            Assert.Equal(AuthorityVerdict.Both, Find(mlw, "MyLittleWarband.CustomTroopWagePatch::Postfix")!.Verdict);
            Assert.Contains(Find(mlw, "MyLittleWarband.RecruitPatch2::Prefix")!.Verdict, new[] { AuthorityVerdict.NeedsRelay, AuthorityVerdict.Review });
        }
        if (reports.TryGetValue("KingdomPlus", out var kp)) Assert.True(kp.NotAnalysable);
        if (reports.TryGetValue("ImprovedGarrisons", out var ig))
        {
            // The recruitment screen's "Maximum number of troops to recruit" slider: a setting the server's tick reads.
            Assert.Contains(ig.Roots, r => r.Verdict == AuthorityVerdict.PlayerStateUnsynced && r.Root.Detail == "UI callback"
                && r.Reason.Contains("GarrisonSettings.MaxRecruitThreshold", StringComparison.Ordinal));
            Assert.Contains(ig.Roots, r => r.Root.Method == "ImprovedGarrisons.SaveSystem.GarrisonBehavior::HourlyEvent"
                && r.Flags?.Any(f => f.StartsWith("HostIsOnlyPlayer", StringComparison.Ordinal)) == true);
        }
        if (reports.TryGetValue("TAOM", out var taomCheats))
            Assert.DoesNotContain(taomCheats.Roots, r => r.Verdict == AuthorityVerdict.NeedsRelay && r.Reason.Contains("CheckCheatUsage", StringComparison.Ordinal));
    }
}
