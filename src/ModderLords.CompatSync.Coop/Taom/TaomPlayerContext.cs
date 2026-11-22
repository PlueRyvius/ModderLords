using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// "The player" on a dedicated server means the connected players, not the idle world-generation hero (TAOM-MAP P4).
///
/// TAOM asks who the player is through IPlayerContextAdapter (Clan.PlayerClan / Hero.MainHero) and decides War of the
/// Ring involvement in WarEventSnapshotAdapter (the main party fought, or a party of the player's kingdom did). On a
/// dedicated server all of that is the idle hero the campaign was created around, so the players' battles and sieges
/// never counted as player involvement, and "is the player on the stronger side" was answered for the wrong kingdom.
///
/// Server only:
/// - a battle, siege or raid is player-involved when any connected player's party took part, or a party of any
///   player's kingdom did;
/// - TAOM's single "player kingdom / clan / culture / mercenary" answers come from a real player (TAOM 2.0.41 added
///   GetPlayerClanId). Inside a per-player call (PlayerScope: camp, emissary, ...) "the player" is already that player
///   and is left alone; otherwise the first connected player with a kingdom speaks for the session (co-op groups
///   normally share one).
///
/// The player's culture: up to 2.0.28 TAOM asked PlayerContextAdapter.GetPlayerCultureId, whose only caller was the
/// execution relation model (the player as executor), and that answer is substituted. 2.0.41 dropped both and asks
/// FactionSides.OfPlayer (kingdom and culture together) from about fourteen places (battle relations, good deeds, long
/// memory, gifts, encounter lines, wanderer and oath dialogs, ...). Substituting OfPlayer everywhere would make every one
/// of those answer for the first player with a kingdom, so player B's battle could run against player A's kingdom.
/// So OfPlayer is substituted only while the 2.0.41 counterpart of the old consumer runs: TAOM's execution relation
/// postfix (ExecutionCampaignBehavior_BloodFeudRelationPenalty_Patch.Postfix). Everything else keeps TAOM's own answer.
/// </summary>
internal sealed class PlayerContextComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.PlayerContext";

    private static MethodInfo? _kingdom;
    private static MethodInfo? _clan;
    private static MethodInfo? _culture;
    private static MethodInfo? _ofPlayer;
    private static MethodInfo? _ofFaction;
    private static MethodInfo? _mercenary;
    private static MethodInfo? _isPlayerRelated;
    private static MethodInfo? _fromMapEvent;
    private static MethodInfo? _executionRelation;

    public string Id => "player-context";

    public string? SkipReason(TaomContext context)
    {
        if (!context.IsServer) return "client";
        var t = context.Taom;
        var adapter = t.GetType("TAOM.Adapters.PlayerContextAdapter", false);
        _kingdom = adapter?.GetMethod("GetPlayerKingdomId", Type.EmptyTypes);
        _clan = adapter?.GetMethod("GetPlayerClanId", Type.EmptyTypes);   // 2.0.41 and later
        _culture = adapter?.GetMethod("GetPlayerCultureId", Type.EmptyTypes);   // up to 2.0.28
        var sides = t.GetType("TAOM.Adapters.FactionSides", false);   // 2.0.41 and later
        _ofPlayer = sides?.GetMethod("OfPlayer", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
        _ofFaction = sides?.GetMethod("Of", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(IFaction) }, null);
        // 2.0.41: the one OfPlayer consumer the old GetPlayerCultureId substitution covered (see the class summary).
        _executionRelation = t.GetType("TAOM.Features.Execution.Hooks.ExecutionCampaignBehavior_BloodFeudRelationPenalty_Patch", false)
            ?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        _mercenary = adapter?.GetMethod("IsUnderMercenaryService", Type.EmptyTypes);
        var snapshots = t.GetType("TAOM.Adapters.WarEventSnapshotAdapter", false);
        _isPlayerRelated = snapshots?.GetMethod("IsPlayerRelated", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(PartyBase), typeof(string) }, null);
        _fromMapEvent = snapshots?.GetMethod("FromMapEvent", new[] { typeof(MapEvent) });

        var missing = new List<string>();
        if (_kingdom?.ReturnType != typeof(string) || _mercenary?.ReturnType != typeof(bool))
            missing.Add("PlayerContextAdapter.GetPlayerKingdomId/IsUnderMercenaryService");
        if (_culture?.ReturnType != typeof(string)
            && (_ofPlayer == null || _ofFaction == null || _ofPlayer.ReturnType != _ofFaction.ReturnType || _executionRelation == null))
            missing.Add("PlayerContextAdapter.GetPlayerCultureId or FactionSides.OfPlayer/Of(IFaction) and ExecutionCampaignBehavior_BloodFeudRelationPenalty_Patch.Postfix");
        if (_isPlayerRelated?.ReturnType != typeof(bool)) missing.Add("WarEventSnapshotAdapter.IsPlayerRelated(PartyBase, string)");
        if (_fromMapEvent == null) missing.Add("WarEventSnapshotAdapter.FromMapEvent(MapEvent)");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony(Owner);
        h.Patch(_kingdom!, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(KingdomPostfix)));
        if (_culture?.ReturnType == typeof(string))
            h.Patch(_culture, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(CulturePostfix)));
        else
        {
            h.Patch(_executionRelation!, prefix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(ExecutionEnterPrefix)),
                finalizer: new HarmonyMethod(typeof(PlayerContextComponent), nameof(ExecutionLeaveFinalizer)));
            h.Patch(_ofPlayer!, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(OfPlayerPostfix)));
        }
        if (_clan?.ReturnType == typeof(string))
            h.Patch(_clan, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(ClanPostfix)));
        h.Patch(_mercenary!, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(MercenaryPostfix)));
        h.Patch(_isPlayerRelated!, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(IsPlayerRelatedPostfix)));
        h.Patch(_fromMapEvent!, postfix: new HarmonyMethod(typeof(PlayerContextComponent), nameof(FromMapEventPostfix)));
        return "server treats connected players as TAOM's player (War of the Ring credit, player kingdom)";
    }

    // ---- who the players are (cached for a second: these run inside campaign ticks) --------------------

    private static DateTime _cachedAt = DateTime.MinValue;
    private static List<(Hero Hero, MobileParty? Party)> _players = new List<(Hero, MobileParty?)>();

    internal static List<(Hero Hero, MobileParty? Party)> Players()
    {
        if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 1) return _players;
        _cachedAt = DateTime.UtcNow;
        var list = new List<(Hero, MobileParty?)>();
        if (ContainerProvider.TryResolve<IPlayerManager>(out var manager) && ContainerProvider.TryResolve<IObjectManager>(out var objects))
        {
            foreach (var p in manager.Players.ToList())
            {
                if (!objects.TryGetObject<Hero>(p.HeroId, out var hero) || hero == null) continue;
                objects.TryGetObject<MobileParty>(p.MobilePartyId, out var party);
                list.Add((hero, party));
            }
        }
        return _players = list;
    }

    /// <summary>The player who speaks for the session outside a per-player call: the first with a kingdom, else the first.</summary>
    private static Hero? Representative()
    {
        var players = Players();
        return players.FirstOrDefault(p => p.Hero.Clan?.Kingdom != null).Hero ?? players.FirstOrDefault().Hero;
    }

    private static bool Substitute => !ServerRelay.PlayerScope.Active;

    private static void KingdomPostfix(ref string __result)
    {
        if (Substitute && Representative() is { } hero) __result = hero.Clan?.Kingdom?.StringId ?? "";
    }

    private static void CulturePostfix(ref string __result)
    {
        if (Substitute && Representative() is { } hero) __result = hero.Culture?.StringId ?? "";
    }

    private static void ClanPostfix(ref string __result)
    {
        if (Substitute && Representative() is { } hero) __result = hero.Clan?.StringId ?? "";
    }

    /// <summary>Depth of TAOM's execution relation postfix on the stack (game thread only).</summary>
    private static int _inExecutionRelation;

    private static void ExecutionEnterPrefix() => _inExecutionRelation++;

    private static void ExecutionLeaveFinalizer() => _inExecutionRelation--;

    /// <summary>
    /// TAOM 2.0.41: the player's side (kingdom and culture ids), only inside the execution relation postfix (the class
    /// summary says why not everywhere).
    /// </summary>
    private static void OfPlayerPostfix(ref object __result)
    {
        if (_inExecutionRelation <= 0 || !Substitute || Representative() is not { } hero || hero.Clan == null) return;
        try { __result = _ofFaction!.Invoke(null, new object?[] { hero.Clan.MapFaction }) ?? __result; }
        catch { }
    }

    private static void MercenaryPostfix(ref bool __result)
    {
        if (Substitute && Representative() is { } hero) __result = hero.Clan?.IsUnderMercenaryService ?? false;
    }

    /// <summary>A party is player-related when it is any player's party, or belongs to any player's kingdom.</summary>
    private static void IsPlayerRelatedPostfix(PartyBase party, ref bool __result)
    {
        if (__result || party?.MobileParty == null) return;
        var mobile = party.MobileParty;
        foreach (var (hero, playerParty) in Players())
        {
            if (playerParty != null && mobile == playerParty) { __result = true; return; }
            var kingdom = hero.Clan?.Kingdom?.StringId;
            if (mobile.Owner != null && !string.IsNullOrEmpty(kingdom) && party.MapFaction?.StringId == kingdom) { __result = true; return; }
        }
    }

    /// <summary>A battle any player's party fought in is player-involved, not only one the idle main party fought.</summary>
    private static void FromMapEventPostfix(MapEvent mapEvent, object __result)
    {
        if (__result == null || mapEvent == null) return;
        var involved = __result.GetType().GetProperty("PlayerInvolved");
        if (involved == null || involved.GetValue(__result) is true) return;
        var parties = new HashSet<MobileParty>(Players().Where(p => p.Party != null).Select(p => p.Party!));
        if (parties.Count == 0) return;
        if (mapEvent.InvolvedParties.Any(p => p.MobileParty != null && parties.Contains(p.MobileParty)))
            involved.SetValue(__result, true);
    }
}
