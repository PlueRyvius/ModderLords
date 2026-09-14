using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>Diagnostic against locally installed mods; each assertion only runs when that mod is present.</summary>
public sealed class ModCodeProbe
{
    private readonly ITestOutputHelper _out;
    public ModCodeProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Analyse_installed_mods()
    {
        var libs = GamePaths.SteamLibraries().ToList();
        var game = ModuleCatalog.FindGameRoot(libs);
        if (game is null) { _out.WriteLine("no game; skipped"); return; }
        var mods = new Dictionary<string, DiscoveredModule>(StringComparer.OrdinalIgnoreCase);
        void Collect(string root, ModuleSourceKind kind)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (ModuleCatalog.TryParse(dir, kind, out _) is { } m) mods.TryAdd(m.Id, m);
        }
        Collect(Path.Combine(game, "Modules"), ModuleSourceKind.GameModules);
        foreach (var lib in libs) Collect(GamePaths.WorkshopRoot(lib), ModuleSourceKind.Workshop);

        var models = new Dictionary<string, ModCodeModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "TAOM", "MyLittleWarband", "KingdomPlus", "ImprovedGarrisons", "HealOnKill", "RTSCamera" })
        {
            if (!mods.TryGetValue(id, out var mod)) { _out.WriteLine(id + ": not installed"); continue; }
            var model = ModAnalysis.Analyse(mod);
            models[id] = model;
            _out.WriteLine($"{id}: {model.Summary}; manual patches {model.Roots.Count(r => r.Patch?.Manual == true)}; opaque methods {model.Methods.Values.Count(n => n.Opaque)}");
            foreach (var n in model.Notes.Take(3)) _out.WriteLine("  note: " + n);
            foreach (var g in model.Roots.GroupBy(r => r.Trigger))
                foreach (var r in g.Take(3)) _out.WriteLine($"  {r.Trigger}: {r.Method} [{r.Detail}]");
        }

        if (models.TryGetValue("TAOM", out var taom))
        {
            var shed = taom.Roots.Single(r => r.Method == "TAOM.Features.TroopWeight.Hooks.PartyUpgraderUpgradeReadyTroops_Patch::Postfix");
            Assert.Equal(new PatchInfo("TaleWorlds.CampaignSystem.CampaignBehaviors.PartyUpgraderCampaignBehavior", "UpgradeReadyTroops", PatchKind.Postfix, false), shed.Patch);
            Assert.Contains("TAOM.Features.TroopWeight.Hooks.PartyUpgraderUpgradeReadyTroopsHook::OnUpgradeReadyTroops",
                taom.Dispatch("TAOM.Features.TroopWeight.Hooks.IOnPartyUpgraderUpgradeReadyTroops::OnUpgradeReadyTroops"));
            Assert.Contains(taom.Roots, r => r.Trigger == RootTrigger.Simulation);
            Assert.Contains(taom.Roots, r => r.Trigger == RootTrigger.PlayerInput);
        }
        if (models.TryGetValue("MyLittleWarband", out var mlw))
        {
            Assert.Contains(mlw.Roots, r => r.Method == "MyLittleWarband.RecruitProductionPatch::Postfix"
                && r.Patch is { Kind: PatchKind.Postfix, TargetMethod: "UpdateVolunteersOfNotablesInSettlement" } p && p.TargetType.EndsWith(".RecruitmentCampaignBehavior"));
            Assert.Contains(mlw.Roots, r => r.Method == "MyLittleWarband.RecruitPatch2::Prefix"
                && r.Patch is { Kind: PatchKind.Prefix, TargetMethod: "game_menu_recruit_volunteers_on_consequence" });
        }
        if (models.TryGetValue("KingdomPlus", out var kp)) Assert.True(kp.NotAnalysable);
        if (models.TryGetValue("RTSCamera", out var rts)) Assert.Contains(rts.Roots, r => r.Patch?.Manual == true);
    }
}
