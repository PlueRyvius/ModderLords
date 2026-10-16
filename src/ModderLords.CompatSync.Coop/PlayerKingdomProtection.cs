using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server only: <see cref="ModderLordsSettings.ProtectPlayerKingdoms"/>. Two layers:
/// <list type="bullet">
/// <item>Prevent: AI war proposals (KingdomDecisionProposalBehavior.ConsiderWar / GetRandomWarDecision) never pick a
/// player's kingdom as the target unless the proposing kingdom is itself a player's.</item>
/// <item>Backstop: any war that still starts against a player's kingdom (a mod's own war logic, a call to war) is
/// ended with MakePeaceAction on the next tick, never inside the WarDeclared event. Coop syncs the stance.</item>
/// </list>
/// Allowed: wars a player's kingdom declares, wars caused by a player's own hostility or crime, and rebellions unless
/// <see cref="ModderLordsSettings.ProtectFromRebellions"/>.
/// </summary>
public static class PlayerKingdomProtection
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.PlayerKingdomProtection");
    private static bool _installTried;
    private static Campaign? _listening;
    private static HashSet<Kingdom> _playerKingdoms = new HashSet<Kingdom>();
    private static readonly List<(IFaction Declarer, IFaction Target)> _undo = new List<(IFaction, IFaction)>();
    private static int _proposalsBlocked;

    /// <summary>From the adapter tick: patches once, listens per campaign, refreshes the player kingdoms and makes any queued peace.</summary>
    public static void Tick()
    {
        try { if (Common.ModInformation.IsClient) return; } catch { return; }
        if (Campaign.Current is not { } campaign) return;
        EnsureInstalled();
        if (!ReferenceEquals(_listening, campaign))
        {
            _listening = campaign;
            _undo.Clear();
            CampaignEvents.WarDeclared.AddNonSerializedListener(typeof(PlayerKingdomProtection), OnWarDeclared);
        }

        var kingdoms = new HashSet<Kingdom>();
        if (ContainerProvider.TryResolve<IPlayerManager>(out var players) && ContainerProvider.TryResolve<IObjectManager>(out var objects))
            foreach (var p in players.Players.ToList())
                if (objects.TryGetObject<Hero>(p.HeroId, out var hero) && hero?.Clan?.Kingdom is { } k) kingdoms.Add(k);
        _playerKingdoms = kingdoms;

        if (_proposalsBlocked > 0)
        {
            Log.Info($"player kingdom protection: blocked {_proposalsBlocked} AI war proposal(s) against player kingdoms");
            _proposalsBlocked = 0;
        }

        if (_undo.Count == 0) return;
        var queued = _undo.ToList();
        _undo.Clear();
        foreach (var (declarer, target) in queued)
        {
            if (!declarer.IsAtWarWith(target)) continue;
            try
            {
                MakePeaceAction.Apply(declarer, target);
                Log.Info($"player kingdom protection: made peace between {declarer.Name} and {target.Name}");
            }
            catch (Exception ex) { Log.Warn($"player kingdom protection: could not make peace between {declarer.Name} and {target.Name}: {ex.GetBaseException().Message}"); }
        }
    }

    private static bool IsPlayerKingdom(IFaction? faction) => faction is Kingdom k && _playerKingdoms.Contains(k);

    /// <summary>True when an AI kingdom (not a player's) going to war with <paramref name="target"/> must be stopped.</summary>
    private static bool Blocks(IFaction? declarer, IFaction? target) =>
        ModderLordsSettings.ProtectPlayerKingdoms && IsPlayerKingdom(target) && !IsPlayerKingdom(declarer);

    private static void OnWarDeclared(IFaction declarer, IFaction target, DeclareWarAction.DeclareWarDetail detail)
    {
        if (!Blocks(declarer, target)) return;
        switch (detail)
        {
            case DeclareWarAction.DeclareWarDetail.CausedByPlayerHostility:
            case DeclareWarAction.DeclareWarDetail.CausedByCrimeRatingChange:
                return;
            case DeclareWarAction.DeclareWarDetail.CausedByRebellion when !ModderLordsSettings.ProtectFromRebellions:
                return;
        }
        Log.Info($"player kingdom protection: {declarer.Name} declared war on {target.Name} ({detail}); peace on the next tick");
        _undo.Add((declarer, target));
    }

    private static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.KingdomDecisionProposalBehavior");
        if (type is null) { Log.Warn("player kingdom protection: KingdomDecisionProposalBehavior not found; only the peace backstop is active"); return; }
        try
        {
            if (AccessTools.Method(type, "ConsiderWar") is { } considerWar)
                Harmony.Patch(considerWar, prefix: new HarmonyMethod(typeof(PlayerKingdomProtection), nameof(ConsiderWarPrefix)));
            else Log.Warn("player kingdom protection: ConsiderWar not found");
            if (AccessTools.Method(type, "GetRandomWarDecision") is { } randomWar)
                Harmony.Patch(randomWar, postfix: new HarmonyMethod(typeof(PlayerKingdomProtection), nameof(RandomWarPostfix)));
            else Log.Warn("player kingdom protection: GetRandomWarDecision not found");
            Log.Info("player kingdom protection: installed");
        }
        catch (Exception ex) { Log.Warn("player kingdom protection: could not patch: " + ex.GetBaseException().Message); }
    }

    /// <summary>ConsiderWar(Clan clan, Kingdom kingdom, IFaction otherFaction): false for a player's kingdom.</summary>
    public static bool ConsiderWarPrefix(object[] __args, ref bool __result)
    {
        var kingdom = __args.OfType<Kingdom>().FirstOrDefault();
        var target = __args.Length > 0 ? __args[__args.Length - 1] as IFaction : null;
        if (!Blocks(kingdom, target)) return true;
        _proposalsBlocked++;
        __result = false;
        return false;
    }

    public static void RandomWarPostfix(ref KingdomDecision? __result)
    {
        if (__result is DeclareWarDecision war && Blocks(war.Kingdom, war.FactionToDeclareWarOn))
        {
            _proposalsBlocked++;
            __result = null;
        }
    }
}
