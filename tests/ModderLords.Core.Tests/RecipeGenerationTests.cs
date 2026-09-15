using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop;
using ModderLords.Core.Compat;
using ModderLords.Core.Compat.Authority;
using ModderLords.Coop.Compat;
using PatchInfo = ModderLords.Core.Compat.Authority.PatchInfo;   // HarmonyLib has a PatchInfo too

namespace ModderLords.Core.Tests;

public sealed class RecipeGenerationTests
{
    private static RootVerdict V(string method, RootTrigger trigger, AuthorityVerdict verdict, PatchInfo? patch = null) =>
        new(new AuthorityRoot(method, trigger, "test", patch), verdict, "test", [], false);

    private static readonly ScanResult Scan = new("Mod", ServerVerdict.ServerSafe, [], [], [], [], ["Mod.TickBehavior", "Mod.UiBehavior"], ["Mod.MissionLogic"], [], false);

    private static AuthorityReport Report() => new()
    {
        ModuleId = "Mod",
        Roots =
        [
            V("Mod.TickBehavior::OnDailyTick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly),
            V("Mod.TickBehavior::OnHourlyTick", RootTrigger.Simulation, AuthorityVerdict.NeedsStateSync),
            V("Mod.TickBehavior+<>c::<RegisterEvents>b__1_0", RootTrigger.Session, AuthorityVerdict.ServerOnly),
            V("Mod.TickBehavior::OnGuarded", RootTrigger.Simulation, AuthorityVerdict.AlreadyHandled),
            V("Mod.UiBehavior::OnTick", RootTrigger.Simulation, AuthorityVerdict.ServerOnly),
            V("Mod.Menus::Buy", RootTrigger.PlayerInput, AuthorityVerdict.NeedsRelay),
            V("Mod.AlliancePatch::Postfix", RootTrigger.Patch, AuthorityVerdict.LeakingPostfix,
                new PatchInfo("TaleWorlds.X.AllianceBehavior", "StartAlliance", PatchKind.Postfix, false)),
            V("Mod.WagePatch::Postfix", RootTrigger.Patch, AuthorityVerdict.Both,
                new PatchInfo("TaleWorlds.X.WageModel", "GetWage", PatchKind.Postfix, false)),
        ],
    };

    [Fact]
    public void AnalysableReport_GivesHandlersAndUnpatch_NotWholeBehaviours()
    {
        var set = RecipeSet.Build([("Mod", Scan, new[] { "Mod.UiBehavior" })], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = Report() });
        var r = Assert.Single(set.Mods);
        Assert.Empty(r.CampaignBehaviors);
        Assert.Empty(r.MissionBehaviors);
        Assert.Equal(["Mod.TickBehavior+<>c::<RegisterEvents>b__1_0", "Mod.TickBehavior::OnDailyTick", "Mod.TickBehavior::OnHourlyTick"], r.Handlers);
        var u = Assert.Single(r.Unpatch);
        Assert.Equal("TaleWorlds.X.AllianceBehavior::StartAlliance", u.Target);
        Assert.Equal("Mod.AlliancePatch::Postfix", u.Patch);
        Assert.Contains("1 player action(s) need a server relay", r.Notes);
        Assert.Contains("kept client-side: Mod.UiBehavior", r.Notes);
    }

    [Fact]
    public void UiRegisteringHandler_IsReplacedByItsServerWork_AndUnsplittableOnesAreNotGated()
    {
        var report = Report();
        report.Roots.Add(V("Mod.GarrisonBehavior::OnGameOpen", RootTrigger.Session, AuthorityVerdict.NeedsStateSync) with
        {
            Flags = ["RegistersUI: registers CampaignGameStarter.AddGameMenuOption in MainMenu..ctor"],
            GateInstead = ["Mod.GarrisonBehavior::SetAllParties", "Mod.PartyManager::Reset"],
        });
        report.Roots.Add(V("Mod.GarrisonBehavior::OnMixed", RootTrigger.Session, AuthorityVerdict.Review) with
        {
            Flags = ["RegistersUI: registers CampaignGameStarter.AddPlayerLine in GarrisonBehavior.OnMixed"],
        });
        report.Roots.Add(V("Mod.SettingsVM+<>c::<Init>b__0", RootTrigger.PlayerInput, AuthorityVerdict.PlayerStateUnsynced));
        var r = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = report }).Mods.Single();
        Assert.Contains("Mod.GarrisonBehavior::SetAllParties", r.Handlers);
        Assert.Contains("Mod.PartyManager::Reset", r.Handlers);
        Assert.DoesNotContain("Mod.GarrisonBehavior::OnGameOpen", r.Handlers);
        Assert.DoesNotContain("Mod.GarrisonBehavior::OnMixed", r.Handlers);
        Assert.Contains("kept 1 UI-registering handler(s) running on clients", r.Notes);
        Assert.Contains("1 handler(s) register UI but could not be split", r.Notes);
        Assert.Contains("1 player setting(s) never reach the server", r.Notes);
    }

    [Fact]
    public void RelayedActions_ReachTheRecipe()
    {
        var report = new AuthorityReport
        {
            ModuleId = "Mod",
            Roots = [V("Mod.GuardsVM+<>c::<Init>b__0", RootTrigger.PlayerInput, AuthorityVerdict.NeedsRelay) with { RelayVia = "Mod.GuardOrders::OrderPatrol" }],
        };
        report.RelayMethods.Add("Mod.GuardOrders::OrderPatrol");
        var r = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = report }).Mods.Single();
        Assert.Equal(["Mod.GuardOrders::OrderPatrol"], r.Relays);
        Assert.Contains("1 player action(s) relayed to the server through 1 method(s)", r.Notes);
        Assert.Contains("\"Relays\"", new RecipeSet { Mods = [r] }.ToJson());
    }

    [Fact]
    public void PlayerComparisons_ReachTheRecipe_EvenWithNothingToGate()
    {
        var report = new AuthorityReport { ModuleId = "Mod", Roots = [V("Mod.Settings::Tick", RootTrigger.Simulation, AuthorityVerdict.Both)] };
        report.PlayerComparisonMethods.Add("Mod.Garrison::IsMine");
        var r = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = report }).Mods.Single();
        Assert.Equal(["Mod.Garrison::IsMine"], r.PlayerComparisons);
        Assert.Empty(r.Handlers);
        Assert.Contains("the server asks about any player instead", r.Notes);
        Assert.Contains("\"PlayerComparisons\"", new RecipeSet { Mods = [r] }.ToJson());
    }

    [Fact]
    public void KeptBehaviour_AlsoExcludesItsLambdaHandlers()
    {
        var set = RecipeSet.Build([("Mod", Scan, new[] { "Mod.TickBehavior" })], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = Report() });
        // TickBehavior's own handlers and its closure's lambda are kept; the other behaviour's handler is still gated.
        Assert.Equal(["Mod.UiBehavior::OnTick"], set.Mods.Single().Handlers);
    }

    [Fact]
    public void Probe_TaomRecipe_FromInstalledModAndCoop()
    {
        var mods = InstalledMods.Find();
        var gi = CoopSinks.FindGameInterface(ModderLords.Core.Launch.GamePaths.SteamLibraries());
        if (mods is null || gi is null || !mods.TryGetValue("TAOM", out var taom)) return;
        var report = AuthorityScan.Classify(ModAnalysis.Analyse(taom), CoopSinks.ScanDll(gi));
        var set = RecipeSet.Build([("TAOM", AssemblyScan.Scan(taom), Array.Empty<string>())], "probe",
            authority: new Dictionary<string, AuthorityReport> { ["TAOM"] = report });
        var r = set.Mods.Single();
        Assert.Empty(r.CampaignBehaviors);
        Assert.Contains("TAOM.Features.FieldCamp.Hooks.FieldCampCampaignBehavior::OnHourlyTick", r.Handlers);
        Assert.Contains(r.Unpatch, u => u.Target == "TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior::StartAlliance");
        Assert.DoesNotContain("TAOM.Features.CultureConversion.Hooks.CultureConversionBehavior::OnDailyTick", r.Handlers);   // self-gated
    }

    [Fact]
    public void Probe_ImprovedGarrisonsRecipe_KeepsItsMenusAndDialogsOnClients()
    {
        var mods = InstalledMods.Find();
        var gi = CoopSinks.FindGameInterface(ModderLords.Core.Launch.GamePaths.SteamLibraries());
        if (mods is null || gi is null || !mods.TryGetValue("ImprovedGarrisons", out var ig)) return;
        var report = AuthorityScan.Classify(ModAnalysis.Analyse(ig), CoopSinks.ScanDll(gi));
        var set = RecipeSet.Build([("ImprovedGarrisons", AssemblyScan.Scan(ig), Array.Empty<string>())], "probe",
            authority: new Dictionary<string, AuthorityReport> { ["ImprovedGarrisons"] = report });
        var r = set.Mods.Single();
        // OnGameOpen adds the keep's "Improved Garrison" option and the garrison dialog lines: it must run on clients.
        Assert.DoesNotContain("ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnGameOpen", r.Handlers);
        Assert.Contains("ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnGameStartSetAllIGParties", r.Handlers);
        Assert.Contains("UI-registering handler(s) running on clients", r.Notes);
    }

    [Fact]
    public void NotAnalysableOrMissingReport_FallsBackToWholeBehaviours()
    {
        var broken = new AuthorityReport { ModuleId = "Mod", NotAnalysable = true };
        var set = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = broken });
        var r = set.Mods.Single();
        Assert.Equal(["Mod.TickBehavior", "Mod.UiBehavior"], r.CampaignBehaviors);
        Assert.Empty(r.Handlers);
        Assert.Contains("not analysable", r.Notes);

        var noAnalysis = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test").Mods.Single();
        Assert.Equal(2, noAnalysis.CampaignBehaviors.Count);
        Assert.Null(noAnalysis.Notes);
    }

    [Fact]
    public void Json_IsSchemaV2_AndReadableByTheModule()
    {
        var set = RecipeSet.Build([("Mod", Scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = Report() });
        var json = set.ToJson();
        var back = RecipeSet.FromJson(json);
        Assert.Equal(RecipeSet.CurrentSchema, back.SchemaVersion);
        Assert.Equal(set.Mods[0].Handlers, back.Mods[0].Handlers);
        // The module parses with its own reader: Mods[].Handlers[] and Mods[].Unpatch[].Target/Patch.
        var root = ModderLords.CompatSync.MiniJson.ParseObject(json);
        Assert.NotNull(root);
        Assert.Contains("\"Unpatch\"", json);
        Assert.Contains("\"Target\": \"TaleWorlds.X.AllianceBehavior::StartAlliance\"", json);
    }
}

/// <summary>RecipeGates with real Harmony: what the client module does with a v2 recipe, without a game.</summary>
public sealed class RecipeGatesTests
{
    private static int s_tickRuns;
    private static int s_postfixRuns;
    private static bool s_client;

    public sealed class Target
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Tick() => s_tickRuns++;
        [MethodImpl(MethodImplOptions.NoInlining)] public int Act() => 1;
    }

    public static void ActPostfix() => s_postfixRuns++;

    private static int s_menuRuns;
    private static int s_workRuns;

    public sealed class SplitHandler
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnGameOpen() { AddMenus(); SetupParties(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private void AddMenus() => s_menuRuns++;
        [MethodImpl(MethodImplOptions.NoInlining)] private void SetupParties() => s_workRuns++;
    }

    [Fact]
    public void GatingTheServerWorkCallee_LeavesTheMenusRunningOnAClient()
    {
        var gates = new RecipeGates("test.split." + Guid.NewGuid().ToString("N"), () => s_client, _ => { });
        Assert.Equal((1, 0), gates.SkipHandlers([typeof(SplitHandler).FullName + "::SetupParties"]));
        s_menuRuns = s_workRuns = 0;
        s_client = true;
        new SplitHandler().OnGameOpen();
        s_client = false;
        Assert.Equal(1, s_menuRuns);
        Assert.Equal(0, s_workRuns);
        new SplitHandler().OnGameOpen();
        Assert.Equal(1, s_workRuns);   // the server still does the work
    }

    [Fact]
    public void HandlerSkipped_OnClientOnly_AndMissingOnesReported()
    {
        var warnings = new List<string>();
        var gates = new RecipeGates("test.gates." + Guid.NewGuid().ToString("N"), () => s_client, warnings.Add);
        var (applied, missing) = gates.SkipHandlers([typeof(Target).FullName + "::Tick", "No.Such.Type::Tick"]);
        Assert.Equal(1, applied);
        Assert.Equal(1, missing);
        Assert.Contains(warnings, w => w.Contains("No.Such.Type::Tick"));
        Assert.Equal((0, 0), gates.SkipHandlers([typeof(Target).FullName + "::Tick"]));   // idempotent

        s_tickRuns = 0;
        var t = new Target();
        s_client = false;
        t.Tick();
        Assert.Equal(1, s_tickRuns);   // server runs it
        s_client = true;
        t.Tick();
        Assert.Equal(1, s_tickRuns);   // client skips it
        Assert.Contains("Target.Tick ran 1, skipped 1", RecipeGates.CountsSummary());

        // Leaking postfix: attached by "the mod", removed on a client only.
        var mod = new Harmony("test.mod." + Guid.NewGuid().ToString("N"));
        var act = AccessTools.Method(typeof(Target), nameof(Target.Act));
        mod.Patch(act, postfix: new HarmonyMethod(typeof(RecipeGatesTests), nameof(ActPostfix)));
        s_postfixRuns = 0;
        t.Act();
        Assert.Equal(1, s_postfixRuns);

        var entry = (typeof(Target).FullName + "::Act", typeof(RecipeGatesTests).FullName + "::ActPostfix");
        s_client = false;
        Assert.Equal((0, 0), gates.RemovePostfixes([entry]));   // server keeps the postfix
        t.Act();
        Assert.Equal(2, s_postfixRuns);

        s_client = true;
        Assert.Equal((1, 0), gates.RemovePostfixes([entry]));
        t.Act();
        Assert.Equal(2, s_postfixRuns);                         // no longer runs on the client
        // Asked again (the server re-sends the recipe): already removed, so nothing is reported or queued.
        Assert.Equal((0, 0), gates.RemovePostfixes([entry]));
        Assert.Equal(0, gates.PendingCount);
        s_client = false;
    }

    private static int s_lateRuns;
    public static void LatePostfix() => s_lateRuns++;

    public sealed class LateTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public int Late() => 2;
    }

    [Fact]
    public void PostfixAttachedAfterTheRecipe_IsRemovedOnRetry()
    {
        s_client = true;
        var warnings = new List<string>();
        var infos = new List<string>();
        var gates = new RecipeGates("test.late." + Guid.NewGuid().ToString("N"), () => s_client, warnings.Add, infos.Add);
        var entry = (typeof(LateTarget).FullName + "::Late", typeof(RecipeGatesTests).FullName + "::LatePostfix");

        // The recipe arrives before the mod has patched (TAOM attaches its diplomacy patches later).
        Assert.Equal((0, 1), gates.RemovePostfixes([entry]));
        Assert.Equal(1, gates.PendingCount);
        Assert.Single(warnings, w => w.Contains("will retry"));
        Assert.Equal((0, 1), gates.RemovePostfixes([entry]));   // asked again (recipe re-sent): still one pending, no second warning
        Assert.Single(warnings);
        Assert.Equal(0, gates.RetryPending());

        var mod = new Harmony("test.latemod." + Guid.NewGuid().ToString("N"));
        mod.Patch(AccessTools.Method(typeof(LateTarget), nameof(LateTarget.Late)), postfix: new HarmonyMethod(typeof(RecipeGatesTests), nameof(LatePostfix)));
        Assert.Equal(1, gates.RetryPending());
        Assert.Equal(0, gates.PendingCount);
        Assert.Contains(infos, i => i.Contains("removed once attached"));

        s_lateRuns = 0;
        new LateTarget().Late();
        Assert.Equal(0, s_lateRuns);

        // The recipe arrives again after removal: not re-queued, no "not attached yet" warning.
        Assert.Equal((0, 0), gates.RemovePostfixes([entry]));
        Assert.Equal(0, gates.PendingCount);
        Assert.Single(warnings);
        s_client = false;
    }
}
