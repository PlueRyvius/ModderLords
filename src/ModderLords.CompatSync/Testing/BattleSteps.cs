using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Testing;

/// <summary>
/// A field battle against a map bandit party, as a player fights one: the server brings a bandit party to the player,
/// the player's game orders the attack (the order a map click gives), picks Attack in the encounter, and the AI fights
/// the battle for both armies. Then both sides read what is left of the bandits. Works in single player too.
/// </summary>
internal static class BattleSteps
{
    internal static void Register()
    {
        InGameTests.Register("battle.placeEnemy", StepSide.Server, PlaceEnemy);
        InGameTests.Register("battle.fight", StepSide.Client, Fight);
        InGameTests.Register("battle.party", StepSide.Any, PartyState);
    }

    // ---- battle.placeEnemy --------------------------------------------------------------------------------------

    /// <summary>
    /// Server: the nearest bandit party the world made (looters first; no party built by code) is put beside the
    /// party of hero <c>heroName</c> and held there. Reports <c>value</c>, the bandits' party id, and <c>men</c>.
    /// </summary>
    private static IEnumerable<StepWait?> PlaceEnemy(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var hero = FindHero(ctx.Arg("heroName"));
        var party = hero.PartyBelongedTo ?? throw new StepFailedException($"{hero.Name} has no party");
        ctx.Assert(party.CurrentSettlement == null, $"{hero.Name}'s party is in {party.CurrentSettlement?.StringId}; the battle needs it on the map");
        ctx.Assert(party.MapEvent == null, $"{hero.Name}'s party is already in a battle");
        var here = party.Position;
        // An escort, so the fight does not rest on a new character alone: a level-1 hero against a dozen looters took
        // longer than eight minutes (2026-11-22). The AI charges with the escort's formations; the test is the sync of
        // the battle and its result, not the hero's skill.
        var escort = ctx.ArgInt("escort", 0);
        if (escort > 0)
        {
            var troop = hero.Culture?.EliteBasicTroop ?? hero.Culture?.BasicTroop
                        ?? MBObjectManager.Instance.GetObject<CharacterObject>("imperial_legionary")
                        ?? throw new StepFailedException($"no troop to escort {hero.Name} with");
            party.MemberRoster.AddToCounts(troop, escort);
            ctx.Log($"escort: {escort}x {troop.StringId} joined {party.StringId}");
            ctx.Report("escort", $"{escort}x {troop.StringId}");
        }
        // Coop builds each side's reserve from this roster; a player whose own hero is not in it gets a battle that Coop
        // ends at once ("local player origin missing").
        ctx.Log($"{party.StringId}: {party.MemberRoster.TotalManCount} men ({party.MemberRoster.TotalHealthyCount} healthy), hero in roster: {party.MemberRoster.Contains(hero.CharacterObject)}");
        var enemy = MobileParty.AllBanditParties
            .Where(p => p.IsActive && p.MapEvent == null && p.CurrentSettlement == null && p.MemberRoster.TotalHealthyCount > 0
                        && p.MapFaction != null && p.MapFaction.IsAtWarWith(hero.MapFaction))
            .OrderBy(p => p.ActualClan?.StringId == "looters" ? 0 : 1)
            .ThenBy(p => p.Position.Distance(here))
            .FirstOrDefault();
        ctx.Assert(enemy != null, "there is no bandit party on the map to fight");
        ctx.Log($"bandits {enemy!.StringId} ({enemy.ActualClan?.StringId}), {enemy.MemberRoster.TotalHealthyCount} men, {enemy.Position.Distance(here):F1} away");
        // Beside the player, standing still: the player attacks them, not the other way round.
        enemy.Position = here;
        enemy.Ai.SetDoNotMakeNewDecisions(true);
        SetMoveHold(enemy);
        yield return ctx.Settled(() => enemy.Position.Distance(party.Position) < 2f, $"{enemy.StringId} beside {hero.Name}'s party", true, timeout: TimeSpan.FromSeconds(20));
        ctx.Report("men", enemy.MemberRoster.TotalHealthyCount);
        ctx.Report("value", enemy.StringId);
    }

    private static void SetMoveHold(MobileParty party)
    {
        var hold = typeof(MobileParty).GetMethod("SetMoveModeHold", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        hold?.Invoke(party, null);
    }

    // ---- battle.fight -------------------------------------------------------------------------------------------

    /// <summary>
    /// Player: attacks bandit party <c>enemy</c> as a player does: the engage order a click on it gives, the bandits'
    /// opening words skipped as a player's "fight" answer skips them, Attack in the encounter menu, Ready at deployment.
    /// In the battle the hero is made invulnerable and the AI fights for both armies until the bandits are down
    /// (<c>battleSeconds</c>, default 180). Then the player leaves the battle and the encounter. Reports <c>kills</c>
    /// and <c>value</c> ("won" or "left with N bandits standing").
    /// </summary>
    private static IEnumerable<StepWait?> Fight(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null && Hero.MainHero != null, "the campaign");
        var enemyId = ctx.Arg("enemy");
        MobileParty? Enemy() => MobileParty.All.FirstOrDefault(p => p.StringId == enemyId);
        yield return ctx.Until(() => Enemy() is { IsActive: true }, $"bandit party {enemyId} on this game's map", TimeSpan.FromSeconds(30));
        var enemy = Enemy()!;
        // The server's escort (battle.placeEnemy escort) reaches this game through Coop's roster sync.
        var men = ctx.ArgInt("men", 0);
        if (men > 0)
            yield return ctx.Until(() => MobileParty.MainParty.MemberRoster.TotalHealthyCount >= men,
                $"{men} healthy men in this player's party", TimeSpan.FromSeconds(60));
        ctx.Assert(PlayerEncounter.Current == null, "the player is already in an encounter");
        ctx.Log($"engaging {enemy.StringId} ({enemy.MemberRoster.TotalHealthyCount} men), {enemy.Position.Distance(MobileParty.MainParty.Position):F1} away");
        Engage(enemy);

        // The encounter opens on contact: in Coop the server decides it, and sends the player into it.
        yield return ctx.Until(() => PlayerEncounter.Current != null || Campaign.Current.ConversationManager?.IsConversationInProgress == true,
            "the encounter with the bandits", TimeSpan.FromSeconds(60));
        if (Campaign.Current.ConversationManager?.IsConversationInProgress == true)
        {
            ctx.Log("the bandits' opening conversation: ending it as a fight");
            Campaign.Current.ConversationManager.EndConversation();
        }
        if (AttackOption() < 0 && PlayerEncounter.Current != null && Campaign.Current.CurrentMenuContext?.GameMenu?.StringId != "encounter")
        {
            // A player's "fight" answer marks the meeting done and the meeting menu turns into the encounter menu.
            PlayerEncounter.SetMeetingDone();
            if (Campaign.Current.CurrentMenuContext?.GameMenu?.StringId != "encounter_meeting") TaleWorlds.CampaignSystem.GameMenus.GameMenu.ActivateGameMenu("encounter_meeting");
        }
        yield return ctx.Until(() => AttackOption() >= 0, "the encounter menu with its Attack option", TimeSpan.FromSeconds(60));
        ctx.Log("menu " + Campaign.Current.CurrentMenuContext?.GameMenu?.StringId + ": attacking");
        Campaign.Current.GameMenuManager.RunConsequencesOfMenuOption(Campaign.Current.CurrentMenuContext!, AttackOption());

        yield return ctx.Until(() => Mission.Current?.MainAgent != null && Mission.Current.MainAgent.IsActive(), "the battle with the player's hero in it", TimeSpan.FromMinutes(3));
        var mission = Mission.Current!;
        ctx.Log($"battle scene {mission.SceneName}, mode {mission.Mode}");
        if (mission.Mode == MissionMode.Deployment)
        {
            var deployment = mission.MissionBehaviors.FirstOrDefault(b => b.GetType().GetMethod("FinishDeployment", Type.EmptyTypes) != null);
            ctx.Assert(deployment != null, "the battle is in deployment and has nothing that finishes it");
            deployment!.GetType().GetMethod("FinishDeployment", Type.EmptyTypes)!.Invoke(deployment, null);
            ctx.Log("deployment finished by " + deployment.GetType().Name);
        }
        yield return ctx.Until(() => mission.Mode != MissionMode.Deployment && mission.MainAgent != null, "the battle to begin", TimeSpan.FromSeconds(60));

        var enemyTeam = mission.PlayerEnemyTeam;
        ctx.Assert(enemyTeam != null, "the battle has no enemy team");
        var enemiesAtStart = enemyTeam!.ActiveAgents.Count;
        ctx.Log("AI on: " + HandToAi(mission));
        var nextLog = DateTime.UtcNow;
        yield return ctx.Until(() =>
        {
            if (mission.MainAgent is { } me && me.IsActive() && me.IsPaused) me.IsPaused = false;
            if (DateTime.UtcNow >= nextLog)
            {
                nextLog = DateTime.UtcNow.AddSeconds(10);
                ctx.Log($"bandits standing {enemyTeam.ActiveAgents.Count}/{enemiesAtStart}, ours {mission.PlayerTeam?.ActiveAgents.Count}");
            }
            return enemyTeam.ActiveAgents.Count == 0;
        }, "the bandits to fall", TimeSpan.FromSeconds(ctx.ArgInt("battleSeconds", 180)));
        var standing = enemyTeam.ActiveAgents.Count;
        ctx.Report("kills", enemiesAtStart - standing);
        ctx.Log($"{enemiesAtStart - standing} of {enemiesAtStart} bandits down");

        // Leave the battle as the player does once it is won, then the encounter's result menus.
        mission.EndMission();
        yield return ctx.Until(() => Mission.Current == null, "the battle to close", TimeSpan.FromSeconds(90));
        yield return ctx.Until(() =>
        {
            if (PlayerEncounter.Current == null) return true;
            var menu = Campaign.Current.CurrentMenuContext?.GameMenu?.StringId;
            if (menu == "encounter" && LeaveOption() is var leave and >= 0)
                Campaign.Current.GameMenuManager.RunConsequencesOfMenuOption(Campaign.Current.CurrentMenuContext!, leave);
            return false;
        }, "the encounter to end and the map to return", TimeSpan.FromMinutes(2));
        ctx.Report("value", standing == 0 ? "won" : $"left with {standing} bandits standing");
    }

    /// <summary>The engage order a map click on <paramref name="enemy"/> gives (the overload differs across game versions).</summary>
    private static void Engage(MobileParty enemy)
    {
        var engage = typeof(MobileParty).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "SetMoveEngageParty" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(MobileParty))
            .OrderBy(m => m.GetParameters().Length).FirstOrDefault()
            ?? throw new StepFailedException("MobileParty.SetMoveEngageParty is not in this game");
        var args = engage.GetParameters().Select((p, i) => i == 0 ? enemy : p.HasDefaultValue ? p.DefaultValue : Activator.CreateInstance(p.ParameterType)).ToArray();
        engage.Invoke(MobileParty.MainParty, args);
    }

    /// <summary>
    /// The hero invulnerable and AI-controlled, every formation of the player's team charging. Out of the coroutine on
    /// purpose: MovementOrder's static fields need a battle (docs/WRITING-TEST-MODULES.md, section 4).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string HandToAi(Mission mission)
    {
        var agent = mission.MainAgent!;
        agent.SetMortalityState(Agent.MortalityState.Invulnerable);
        agent.Controller = AgentControllerType.AI;
        agent.SetWatchState(Agent.WatchState.Alarmed);
        agent.SetAutomaticTargetSelection(true);
        agent.ResetEnemyCaches();
        agent.ForceAiBehaviorSelection();
        agent.IsPaused = false;
        var formations = 0;
        foreach (var formation in mission.PlayerTeam.FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0))
        {
            formation.SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
            formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
            formations++;
        }
        return $"hero AI-controlled and invulnerable; {formations} formation(s) charging";
    }

    private static int AttackOption() => MenuOption("encounter", "attack");

    /// <summary>The encounter menu's way out after a won battle ("continue", or "leave").</summary>
    private static int LeaveOption()
    {
        var i = MenuOption("encounter", "continue");
        return i >= 0 ? i : MenuOption("encounter", "leave");
    }

    private static int MenuOption(string menuId, string optionId)
    {
        var menu = Campaign.Current?.CurrentMenuContext?.GameMenu;
        if (menu == null || menu.StringId != menuId) return -1;
        int i = 0;
        foreach (var option in menu.MenuOptions)
        {
            if (option.IdString == optionId) return i;
            i++;
        }
        return -1;
    }

    // ---- battle.party -------------------------------------------------------------------------------------------

    /// <summary>Party <c>enemy</c> as this game sees it: "gone" once destroyed, else its healthy men. Compared across sides.</summary>
    private static IEnumerable<StepWait?> PartyState(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var id = ctx.Arg("enemy");
        string State()
        {
            var p = MobileParty.All.FirstOrDefault(m => m.StringId == id);
            return p == null || !p.IsActive ? "gone" : $"{p.MemberRoster.TotalHealthyCount} men" + (p.MapEvent != null ? " in a battle" : "");
        }
        yield return ctx.Settled(() => State(), $"party {id}", ctx.HasArg("expected") ? ctx.Arg("expected") : null, timeout: TimeSpan.FromSeconds(60));
        ctx.Report("value", State());
    }

    private static Hero FindHero(string name)
    {
        return HeroNames.Single(name);
    }
}
