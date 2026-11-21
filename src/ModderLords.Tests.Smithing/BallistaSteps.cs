using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Tests.Smithing;

/// <summary>
/// Steps for MS2's tier 6 ballista bow, the 3.77 m yumi with its own bow shader (MS2's docs/ballista-yumi.md):
/// the shader files MS2 copies into the game, the crafted bow's model, and the bow in a real battle.
///
/// The yumi's names (mesh, donor item) are written out here rather than read from MS2, so this DLL still builds
/// and loads against an MS2 release without the yumi; there the checks fail and say what is missing.
/// </summary>
internal static class BallistaSteps
{
    private const string ModId = "ModularSmithing2";
    internal const string YumiMesh = "ms2_ballista_yumi_v3";
    internal const string YumiDonor = "ms2_ballista_yumi_donor";

    internal static void Register()
    {
        InGameTests.Register("smithing.yumiShaders", StepSide.Any, YumiShaders);
        InGameTests.Register("smithing.yumiItem", StepSide.Any, YumiItem);
        InGameTests.Register("smithing.yumiBattle", StepSide.Client, YumiBattle);
    }

    // ---- the shader files ---------------------------------------------------------------------------------------

    /// <summary>
    /// MS2 ships its bow shader's sources in its own Shaders\Sources and copies them into the game's
    /// Shaders\Sources when it loads, because the engine compiles shaders only from there. Every file MS2 ships
    /// (*.rs, *.rsh) must be in the game's folder with the same bytes; without them whoever holds the bow is invisible.
    /// The Coop server skips the copy (it draws nothing), so on side server this only reports what it finds.
    /// Reports <c>value</c>: how many files MS2 ships.
    /// </summary>
    private static IEnumerable<StepWait?> YumiShaders(StepContext ctx)
    {
        var moduleDir = Path.Combine(ModuleHelper.GetModuleFullPath(ModId), "Shaders", "Sources");
        var gameDir = Path.GetFullPath(Path.Combine(BasePath.Name, "Shaders", "Sources"));
        ctx.Log($"MS2's shaders: {moduleDir}");
        ctx.Log($"the game's shaders: {gameDir}");
        ctx.Assert(Directory.Exists(moduleDir), $"this MS2 ships no shader sources ({moduleDir} is missing)");
        var shipped = Directory.GetFiles(moduleDir)
            .Where(f => f.EndsWith(".rs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rsh", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        ctx.Assert(shipped.Count >= ctx.ArgInt("atLeast", 7), $"MS2 ships {shipped.Count} shader file(s), expected at least {ctx.ArgInt("atLeast", 7)}");

        var problems = new List<string>();
        var detail = new StringBuilder();
        foreach (var file in shipped)
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(gameDir, name);
            string state = !File.Exists(target) ? "missing" : File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file)) ? "same" : "different";
            detail.AppendLine($"{name}: {state}");
            if (state != "same") problems.Add($"{name} {state}");
        }
        ctx.Dump("shaders", detail.ToString());
        ctx.Report("value", shipped.Count);
        if (ctx.IsServer)
        {
            // The server skips the copy, and in a test run it shares the game folder with the player's game: nothing
            // here is the server's doing, so it is logged, not judged.
            ctx.Log("server: not judged (MS2 skips the copy on the dedicated server); " + (problems.Count == 0 ? "all present" : string.Join(", ", problems)));
            yield break;
        }
        ctx.Assert(problems.Count == 0, $"the game's Shaders\\Sources lacks MS2's copy of {problems.Count} file(s): {string.Join(", ", problems)}");
    }

    // ---- the bow's model -------------------------------------------------------------------------------------------

    /// <summary>
    /// The item <c>item</c> (default MS2's hidden donor) wears the yumi: mesh <c>mesh</c> (default
    /// ms2_ballista_yumi_v3), no item-level scale (the engine ignores it on a held bow, so MS2 dropped it), no on-the-back
    /// mesh borrowed from the longbow, and with <c>difficulty</c> the Bow skill needed to equip it.
    /// Reports <c>value</c> for compares across sides.
    /// </summary>
    private static IEnumerable<StepWait?> YumiItem(StepContext ctx)
    {
        var id = ctx.Arg("item", YumiDonor);
        ItemObject? item = null;
        yield return ctx.Until(() => (item = MBObjectManager.Instance.GetObject<ItemObject>(id)) is { IsReady: true }, $"item {id}", TimeSpan.FromSeconds(30));
        var it = item!;
        var mesh = ctx.Arg("mesh", YumiMesh);
        var weapon = it.WeaponComponent?.PrimaryWeapon;
        var problems = new List<string>();
        if (it.MultiMeshName != mesh) problems.Add($"mesh {Show(it.MultiMeshName)}, expected {mesh}");
        if (Math.Abs(it.ScaleFactor - 1f) > 0.001f) problems.Add($"scale_factor {it.ScaleFactor:0.###}, expected none (1)");
        if (!string.IsNullOrEmpty(it.HolsterWithWeaponMeshName)) problems.Add($"holster_mesh_with_weapon {it.HolsterWithWeaponMeshName}: the longbow's on-the-back model would show instead of the yumi");
        if (weapon == null || weapon.WeaponClass != WeaponClass.Bow) problems.Add($"weapon class {weapon?.WeaponClass.ToString() ?? "none"}, expected Bow");
        if (ctx.HasArg("difficulty") && it.Difficulty != ctx.ArgInt("difficulty")) problems.Add($"difficulty {it.Difficulty}, expected {ctx.ArgInt("difficulty")}");

        var text = $"{id}: mesh {Show(it.MultiMeshName)}, scale {it.ScaleFactor:0.###}, holster {Show(it.HolsterMeshName)}, holster with weapon {Show(it.HolsterWithWeaponMeshName)}, "
                 + $"difficulty {it.Difficulty}, class {weapon?.WeaponClass.ToString() ?? "none"}, usage {weapon?.ItemUsage ?? "none"}, length {weapon?.WeaponLength ?? 0}";
        ctx.Dump("item", text);
        ctx.Report("mesh", it.MultiMeshName);
        ctx.Report("value", $"mesh {it.MultiMeshName}, scale {it.ScaleFactor:0.###}, holster with weapon {Show(it.HolsterWithWeaponMeshName)}, difficulty {it.Difficulty}");
        ctx.Assert(problems.Count == 0, $"{id}: " + string.Join("; ", problems));
    }

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "none" : s!;

    // ---- the bow in a battle ---------------------------------------------------------------------------------------

    /// <summary>
    /// Single player: the player's hero fights a bandit party with the bow <c>bow</c> and bolts <c>ammo</c> from the
    /// party's inventory. The player's party goes to a bandit party on the map (looters first), the encounter starts
    /// without the bandits' opening conversation, and the encounter menu's Attack option opens the battle. Deployment
    /// is finished as the Ready button does. The hero is then made invulnerable and handed to the AI, so it is an AI
    /// archer with the bow. <c>mesh</c> (default the yumi's; <c>any</c> for a control run with a native bow) is the
    /// model the bow must have in battle; <c>give</c> true adds the bow to the inventory first (a native bow).
    ///
    /// Checks: the hero carries the bow and bolts into the battle, shoots at least <c>shots</c> (default 3) bolts, and
    /// hits an enemy at least once. Takes screenshots for a person to judge (the harness cannot see a bend), into the
    /// run folder's evidence\yumi-battle: over the shoulder and from a side camera, the bow in hand and on the back,
    /// then from the side camera at full draw (up to <c>screenshots</c>, default 3). Each stage is also written to
    /// trace.txt there as it happens, so a crash still shows how far the step got. Then leaves the battle and the
    /// encounter, and gives the hero back their equipment.
    /// </summary>
    private static IEnumerable<StepWait?> YumiBattle(StepContext ctx)
    {
        ctx.Assert(Campaign.Current != null && Hero.MainHero != null, "there is no campaign with a player hero");
        ctx.Assert(MobileParty.MainParty.CurrentSettlement == null, $"the party is in {MobileParty.MainParty.CurrentSettlement?.StringId}; the battle needs it on the map");
        ctx.Assert(PlayerEncounter.Current == null, "the player is already in an encounter");
        var bow = Item(ctx, "bow");
        var ammo = Item(ctx, "ammo");
        if (ctx.Arg("give", "false") == "true") MobileParty.MainParty.ItemRoster.AddToCounts(bow, 1);
        ctx.Assert(MobileParty.MainParty.ItemRoster.GetItemNumber(bow) > 0, $"{bow.StringId} is not in the party's inventory (craft it first)");
        ctx.Assert(MobileParty.MainParty.ItemRoster.GetItemNumber(ammo) > 0, $"{ammo.StringId} is not in the party's inventory (craft it first)");

        var shotsWanted = ctx.ArgInt("shots", 3);
        var screenshotsWanted = ctx.ArgInt("screenshots", 3);
        var folder = Path.Combine(string.IsNullOrEmpty(ctx.RunFolder) ? Path.GetTempPath() : ctx.RunFolder, "evidence", "yumi-battle");
        Directory.CreateDirectory(folder);
        // Each line also goes to evidence\yumi-battle\trace.txt at once, so a crash still shows how far the step got.
        var tracePath = Path.Combine(folder, "trace.txt");
        void Trace(string line)
        {
            ctx.Log(line);
            try { File.AppendAllText(tracePath, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); } catch (IOException) { }
        }
        var shots = new List<string>();
        void Screenshot(string name)
        {
            var path = Path.Combine(folder, name + ".bmp");
            Trace("taking screenshot " + name);
            try { TaleWorlds.Engine.Utilities.TakeScreenshot(path); shots.Add(path); Trace("screenshot " + path); }
            catch (Exception ex) { Trace($"screenshot {name} failed: {ex.GetBaseException().Message}"); }
        }

        // The hero: Bow skill for the bow's difficulty, the bow and two stacks of bolts, nothing else in hand, and on foot
        // (a ballista bow is a foot-only longbow).
        var hero = Hero.MainHero!;
        var equipment = hero.BattleEquipment;
        var bowSkill = Math.Max(hero.GetSkillValue(DefaultSkills.Bow), bow.Difficulty + 50);
        hero.SetSkillValue(DefaultSkills.Bow, bowSkill);
        var slots = new[] { EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, EquipmentIndex.Weapon3, EquipmentIndex.Horse, EquipmentIndex.HorseHarness };
        var saved = slots.ToDictionary(slot => slot, slot => equipment[slot]);
        equipment[EquipmentIndex.Weapon0] = new EquipmentElement(bow);
        equipment[EquipmentIndex.Weapon1] = new EquipmentElement(ammo);
        equipment[EquipmentIndex.Weapon2] = new EquipmentElement(ammo);
        equipment[EquipmentIndex.Weapon3] = EquipmentElement.Invalid;
        equipment[EquipmentIndex.Horse] = EquipmentElement.Invalid;
        equipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
        Trace($"hero: Bow {bowSkill} (the bow needs {bow.Difficulty}); weapons {bow.StringId}, {ammo.StringId} x2; on foot");

        MobileParty? enemy = null;
        try
        {
            // A bandit party the world made itself (looters first): no party built by code, so nothing about it can
            // differ from what the map spawns.
            enemy = MobileParty.AllBanditParties
                .Where(p => p.IsActive && p.MapEvent == null && p.CurrentSettlement == null && p.MemberRoster.TotalHealthyCount > 0 && p.MapFaction != null && p.MapFaction.IsAtWarWith(Hero.MainHero!.MapFaction))
                .OrderBy(p => p.ActualClan?.StringId == "looters" ? 0 : 1)
                .ThenBy(p => p.MemberRoster.TotalHealthyCount)
                .FirstOrDefault();
            ctx.Assert(enemy != null, "there is no bandit party on the map to fight");
            // The player goes out to the bandits (away from towns and lords, whose parties joined the fight once), not the other way.
            MobileParty.MainParty.Position = enemy!.Position;
            Trace($"bandits: {enemy.StringId} ({enemy.ActualClan?.StringId}), {enemy.MemberRoster.TotalHealthyCount} men, {enemy.Position.Distance(MobileParty.MainParty.Position):F2} from the player");

            // The encounter without the bandits' opening conversation. EncounterManager.StartPartyEncounter opens the
            // meeting menu at once, whose init starts the conversation (game_menu_encounter_meeting_on_init). So: the
            // encounter's own restart (party fields, no menu), the meeting marked done, then the meeting menu, whose
            // init starts the battle and switches to the encounter menu, as it does after a player's "fight" answer.
            // Then the menu's Attack option.
            PlayerEncounter.RestartPlayerEncounter(enemy.Party, PartyBase.MainParty);
            PlayerEncounter.SetMeetingDone();
            GameMenu.ActivateGameMenu("encounter_meeting");
            yield return ctx.Until(() => AttackOption() >= 0, "the encounter menu with its Attack option", TimeSpan.FromSeconds(30));
            Trace("menu: " + (Campaign.Current.CurrentMenuContext?.GameMenu?.StringId ?? "none"));
            Trace("attacking");
            Campaign.Current.GameMenuManager.RunConsequencesOfMenuOption(Campaign.Current.CurrentMenuContext!, AttackOption());

            var sawMission = false;
            yield return ctx.Until(() =>
            {
                if (!sawMission && Mission.Current != null) { sawMission = true; Trace("mission open"); }
                return Mission.Current != null && Mission.Current.MainAgent != null && Mission.Current.MainAgent.IsActive();
            }, "the battle with the player's hero in it", TimeSpan.FromSeconds(120));
            var mission = Mission.Current!;
            Trace($"battle scene {mission.SceneName}, mode {mission.Mode}");
            if (mission.Mode == MissionMode.Deployment)
            {
                var deployment = mission.MissionBehaviors.FirstOrDefault(b => b.GetType().GetMethod("FinishDeployment", Type.EmptyTypes) != null);
                ctx.Assert(deployment != null, "the battle is in deployment and has nothing that finishes it");
                deployment!.GetType().GetMethod("FinishDeployment", Type.EmptyTypes)!.Invoke(deployment, null);
                Trace("deployment finished by " + deployment.GetType().Name);
            }
            yield return ctx.Until(() => mission.Mode != MissionMode.Deployment && mission.MainAgent != null, "the battle to begin", TimeSpan.FromSeconds(30));

            // What the hero carries into the battle.
            var agent = mission.MainAgent!;
            var problems = new List<string>();
            var held = agent.Equipment[EquipmentIndex.Weapon0];
            if (held.Item != bow) problems.Add($"the hero's first weapon in battle is {held.Item?.StringId ?? "nothing"}, not {bow.StringId}");
            else if (ctx.Arg("mesh", YumiMesh) != "any" && held.Item.MultiMeshName != ctx.Arg("mesh", YumiMesh)) problems.Add($"the bow in battle has mesh {held.Item.MultiMeshName}, not {ctx.Arg("mesh", YumiMesh)}");
            int Ammo() => new[] { EquipmentIndex.Weapon1, EquipmentIndex.Weapon2 }.Sum(i => agent.Equipment[i].Item == ammo ? (int)agent.Equipment[i].Amount : 0);
            int startAmmo = Ammo();
            if (startAmmo <= 0) problems.Add($"the hero has no {ammo.StringId} in battle");
            ctx.Assert(problems.Count == 0, string.Join("; ", problems));
            Trace($"in battle: {bow.StringId} ({held.Item!.MultiMeshName}), {startAmmo} bolts");

            // The bow in hand, then on the back: each with a moment for the animation and the camera.
            Trace("making the hero invulnerable");
            agent.SetMortalityState(Agent.MortalityState.Invulnerable);
            string Wielded() => agent.WieldedWeapon.Item?.StringId ?? "nothing";
            Screenshot("00-start");
            Trace("at the start the hero wields " + Wielded());
            Trace("wielding the bow");
            agent.TryToWieldWeaponInSlot(EquipmentIndex.Weapon0, Agent.WeaponWieldActionType.Instant, false);
            var waitUntil = DateTime.UtcNow.AddSeconds(2);
            yield return ctx.Until(() => DateTime.UtcNow >= waitUntil, "the bow to come to hand");
            Trace("after wielding the bow the hero wields " + Wielded());
            Screenshot("01-in-hand");
            // The player's camera sits over the shoulder and leaves most of a 3.77 m bow out of frame: a camera beside
            // the hero frames all of it.
            if (ViewCamera(agent, 70f)) { waitUntil = DateTime.UtcNow.AddSeconds(0.5); yield return ctx.Until(() => DateTime.UtcNow >= waitUntil, "the side camera"); Screenshot("01b-in-hand-side"); }
            else Trace("no side camera: the battle screen has no CustomCamera");
            ViewCamera(agent, null);
            Trace("sheathing the bow");
            agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);
            waitUntil = DateTime.UtcNow.AddSeconds(2);
            yield return ctx.Until(() => DateTime.UtcNow >= waitUntil, "the bow to settle on the back");
            Trace("after sheathing the hero wields " + Wielded());
            Screenshot("02-carried");
            if (ViewCamera(agent, 150f)) { waitUntil = DateTime.UtcNow.AddSeconds(0.5); yield return ctx.Until(() => DateTime.UtcNow >= waitUntil, "the side camera"); Screenshot("02b-carried-side"); }
            ViewCamera(agent, null);

            // An AI archer with the yumi: AI control, in the player team's ranged formation, firing at will and charging
            // (an agent with no formation has no orders, and stood still in the first runs).
            Trace("AI control and orders");
            var enemyTeam = mission.PlayerEnemyTeam;
            ctx.Assert(enemyTeam != null, "the battle has no enemy team");
            Trace("AI on: " + OrderToFireAndCharge(agent));
            agent.TryToWieldWeaponInSlot(EquipmentIndex.Weapon0, Agent.WeaponWieldActionType.Instant, false);
            int Hits() => mission.AllAgents.Count(a => a.Team == enemyTeam && a.IsHuman && (!a.IsActive() || a.Health < a.HealthLimit));
            int fullDraws = 0;
            var lastShot = DateTime.MinValue;
            var nextLog = DateTime.UtcNow;
            var nextCamera = DateTime.MinValue;
            yield return ctx.Until(() =>
            {
                // The side camera follows the hero through the fight, so the full-draw screenshots show the bend.
                if (DateTime.UtcNow >= nextCamera && agent.IsActive()) { nextCamera = DateTime.UtcNow.AddSeconds(0.5); ViewCamera(agent, 90f); if (agent.IsPaused) agent.IsPaused = false; }
                if (DateTime.UtcNow >= nextLog)
                {
                    nextLog = DateTime.UtcNow.AddSeconds(5);
                    Trace($"t: {AiState(agent)}, wields {Wielded()}, stage {agent.GetCurrentActionStage(1)}, ammo {Ammo()}, hits {Hits()}, "
                          + $"enemies active {enemyTeam!.ActiveAgents.Count}, nearest {enemyTeam.ActiveAgents.Select(a => a.Position.Distance(agent.Position)).DefaultIfEmpty(-1).Min():F1} m");
                }
                if (fullDraws < screenshotsWanted && agent.IsActive() && agent.GetCurrentActionStage(1) == Agent.ActionStage.AttackReady && DateTime.UtcNow - lastShot > TimeSpan.FromSeconds(1.5))
                {
                    fullDraws++;
                    lastShot = DateTime.UtcNow;
                    Screenshot($"03-full-draw-{fullDraws}");
                }
                return startAmmo - Ammo() >= shotsWanted && Hits() > 0 && fullDraws >= Math.Min(1, screenshotsWanted);
            }, $"the hero to shoot {shotsWanted} bolt(s), hit a looter and reach full draw", TimeSpan.FromSeconds(ctx.ArgInt("battleSeconds", 120)));
            ViewCamera(agent, null);

            int fired = startAmmo - Ammo();
            int hits = Hits();
            Trace($"fired {fired} bolt(s), {hits} looter(s) hit or down, {fullDraws} full-draw screenshot(s)");
            ctx.Report("fired", fired);
            ctx.Report("hits", hits);
            ctx.Report("screenshots", string.Join(";", shots));
            ctx.Dump("battle", $"bow {bow.StringId} mesh {bow.MultiMeshName}\nammo {ammo.StringId}\nfired {fired}\nlooters hit or down {hits}\nscreenshots:\n{string.Join("\n", shots)}");
            ctx.Report("value", $"fired {fired}, hit {hits}");
        }
        finally
        {
            foreach (var kv in saved) equipment[kv.Key] = kv.Value;
        }

        // Leave the battle and the encounter, back to the map.
        Mission.Current?.EndMission();
        yield return ctx.Until(() => Mission.Current == null && Campaign.Current.CurrentMenuContext != null || PlayerEncounter.Current == null,
            "the battle to close", TimeSpan.FromSeconds(60));
        if (PlayerEncounter.Current != null) PlayerEncounter.Finish();
        if (enemy != null && enemy.IsActive) DestroyPartyAction.Apply(null, enemy);
        yield return ctx.Until(() => PlayerEncounter.Current == null && Mission.Current == null, "back on the map", TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Puts the agent in its team's ranged formation, firing at will and charging. Kept out of the step's coroutine on
    /// purpose: MovementOrder's static fields are built with Mission.Current.CurrentTime, and .NET Framework may run that
    /// static setup when a method that names them is compiled. Named in the coroutine, it ran on the campaign map
    /// (compiled when the step started), threw, and left MovementOrder broken for the session: the next battle then
    /// crashed while adding its teams. Here it is compiled only when called, inside the battle.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string OrderToFireAndCharge(Agent agent)
    {
        // The player's agent handed to the AI as the RTS-camera style mods do: AI control, alarmed (looking for
        // enemies), fresh enemy caches and a new behaviour pick. Setting Controller alone left the hero standing with
        // the bow in hand while looters walked up to him (2026-11-19, a native longbow too).
        agent.Controller = AgentControllerType.AI;
        agent.SetWatchState(Agent.WatchState.Alarmed);
        agent.DisableScriptedMovement();
        agent.SetAutomaticTargetSelection(true);
        agent.ResetEnemyCaches();
        var formation = agent.Team.GetFormation(FormationClass.Ranged);
        if (agent.Formation != formation) agent.Formation = formation;
        formation.SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
        formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
        agent.ForceAiBehaviorSelection();
        // The player's agent comes with its AI paused (AIStateFlag.Paused): handed to the AI, it then never acts.
        agent.IsPaused = false;
        return AiState(agent);
    }

    private static string AiState(Agent agent) =>
        $"controller {agent.Controller}, watch {agent.CurrentWatchState}, ai flags {agent.AIStateFlags}, formation {agent.Formation?.FormationIndex.ToString() ?? "none"}";

    /// <summary>
    /// Points the mission screen at the agent from <paramref name="angle"/> degrees round from where it faces (0 in front,
    /// 90 at its side, 180 behind), <paramref name="distance"/> m out, looking at <paramref name="height"/> m up from
    /// <paramref name="rise"/> m above that. The defaults (5 m out, about 3 m up) frame a 3.77 m bow whole; null gives the
    /// player's camera back. Reaches the screen (MissionScreen.CustomCamera, in the View assembly) by reflection. False
    /// when there is no such screen.
    /// </summary>
    private static TaleWorlds.Engine.GameEntity? _cameraEntity;
    private static TaleWorlds.Engine.Scene? _cameraScene;

    internal static bool ViewCamera(Agent agent, float? angle, float distance = 5f, float height = 1.4f, float rise = 1.6f, float fov = 1.0f)
    {
        var screens = Type.GetType("TaleWorlds.ScreenSystem.ScreenManager, TaleWorlds.ScreenSystem");
        var screen = screens?.GetProperty("TopScreen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var custom = screen?.GetType().GetProperty("CustomCamera");
        if (custom == null) return false;
        if (angle == null) { custom.SetValue(screen, null); return true; }
        var look = agent.LookDirection;
        var forward = new Vec2(look.x, look.y);
        forward = forward.LengthSquared > 0.0001f ? forward.Normalized() : new Vec2(0f, 1f);
        float rad = angle.Value * (float)Math.PI / 180f;
        var dir = new Vec2(forward.x * (float)Math.Cos(rad) - forward.y * (float)Math.Sin(rad), forward.x * (float)Math.Sin(rad) + forward.y * (float)Math.Cos(rad));
        var at = agent.Position + new Vec3(0f, 0f, height);
        // By default 5 m out and 1.6 m above chest height (about 3 m up): over the bushes and carts that hid the hero at
        // chest height, looking down at him.
        var eye = at + new Vec3(dir.x * distance, dir.y * distance, rise);
        return ViewFrom(screen!, custom, agent.Mission.Scene, eye, at, fov);
    }

    /// <summary>Points the mission screen from <paramref name="eye"/> at <paramref name="at"/>; null eye gives the
    /// player's camera back. False when there is no mission screen.</summary>
    internal static bool ViewFrom(TaleWorlds.Engine.Scene scene, Vec3? eye, Vec3 at, float fov = 1.0f)
    {
        var screens = Type.GetType("TaleWorlds.ScreenSystem.ScreenManager, TaleWorlds.ScreenSystem");
        var screen = screens?.GetProperty("TopScreen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var custom = screen?.GetType().GetProperty("CustomCamera");
        if (custom == null) return false;
        if (eye == null) { custom.SetValue(screen, null); return true; }
        return ViewFrom(screen!, custom, scene, eye.Value, at, fov);
    }

    private static bool ViewFrom(object screen, PropertyInfo custom, TaleWorlds.Engine.Scene scene, Vec3 eye, Vec3 at, float fov)
    {
        var camera = TaleWorlds.Engine.Camera.CreateCamera();
        camera.SetFovVertical(fov, 16f / 9f, 0.05f, 2000f);
        camera.LookAt(eye, at, Vec3.Up);
        // The screen copies a custom camera's lens but takes its position only from an entity the camera sits on
        // (MissionScreen.CheckForUpdateCamera): without one the view stayed at the scene's origin, out over the sea.
        var frame = camera.Frame;
        if (_cameraEntity == null || _cameraScene != scene)
        {
            _cameraEntity = TaleWorlds.Engine.GameEntity.CreateEmpty(scene, false, false, false);
            _cameraScene = scene;
        }
        _cameraEntity.SetGlobalFrame(frame);
        camera.Entity = _cameraEntity;
        custom.SetValue(screen, camera);
        return true;
    }

    private static ItemObject Item(StepContext ctx, string arg)
    {
        var id = ctx.Arg(arg);
        var item = MBObjectManager.Instance.GetObject<ItemObject>(id);
        if (item == null) throw new StepFailedException($"there is no item {id} ({arg})");
        return item;
    }

    /// <summary>The index of the current menu's "attack" option, or -1 when the menu is not the encounter menu.</summary>
    private static int AttackOption()
    {
        var menu = Campaign.Current?.CurrentMenuContext?.GameMenu;
        if (menu == null) return -1;
        int i = 0;
        foreach (var option in menu.MenuOptions)
        {
            if (option.IdString == "attack") return i;
            i++;
        }
        return -1;
    }
}
