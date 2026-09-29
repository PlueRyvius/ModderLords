using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>Who the players are on the server, by clan. Shared by the owner scope, the player checks and the treaty filter.</summary>
internal static class LePlayers
{
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly Dictionary<Clan, (Hero Hero, MobileParty? Party)> ByClan = new Dictionary<Clan, (Hero, MobileParty?)>();

    /// <summary>
    /// The player who belongs to <paramref name="clan"/>, or null for an AI clan. Connected players come from Coop's
    /// player list (with their party); a player who is registered but offline is still found through Coop's own
    /// "is this a player's hero?" answer, so their towns are not handed to the AI while they are away.
    /// </summary>
    internal static (Hero Hero, MobileParty? Party)? For(Clan? clan)
    {
        if (clan == null) return null;
        Refresh();
        if (ByClan.TryGetValue(clan, out var known)) return known;
        try
        {
            foreach (var hero in clan.Heroes)
                if (ServerPlayerChecks.IsAnyPlayerHero(hero))
                {
                    var entry = (hero, hero.PartyBelongedTo);
                    ByClan[clan] = entry;
                    return entry;
                }
        }
        catch { }
        return null;
    }

    internal static bool IsPlayerClan(Clan? clan) => For(clan) != null;

    /// <summary>A kingdom a player leads, or whose ruling clan is a player's: the AI must not make its trade treaties.</summary>
    internal static bool IsPlayerLedKingdom(Kingdom? kingdom) =>
        kingdom != null && (kingdom.Leader != null && (ServerPlayerChecks.IsAnyPlayerHero(kingdom.Leader) || IsPlayerClan(kingdom.Leader.Clan))
                            || IsPlayerClan(RulingClan(kingdom)));

    private static System.Reflection.PropertyInfo? _rulingClan;
    private static bool _rulingClanLooked;

    /// <summary>Kingdom.RulingClan, read by name so a game version without it just falls back to the leader's clan.</summary>
    private static Clan? RulingClan(Kingdom kingdom)
    {
        if (!_rulingClanLooked)
        {
            _rulingClanLooked = true;
            _rulingClan = typeof(Kingdom).GetProperty("RulingClan", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        }
        try { return _rulingClan?.GetValue(kingdom) as Clan; } catch { return null; }
    }

    private static void Refresh()
    {
        if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 1) return;
        _cachedAt = DateTime.UtcNow;
        ByClan.Clear();
        foreach (var (hero, party) in PlayerContextComponent.Players())
            if (hero.Clan != null && !ByClan.ContainsKey(hero.Clan)) ByClan[hero.Clan] = (hero, party);
    }
}

/// <summary>
/// "The player" inside a settlement's daily tick is that settlement's owner, when a player owns it
/// (docs/LIVING-ECONOMY-LAYER.md, part 2).
///
/// Living Economy decides a lot per settlement by asking whether its owner clan is Clan.PlayerClan: AI towns pick their
/// own tax policy, projects, estate actions and armory upgrades, AI lords pour their gold into their towns, villages
/// and castle treasuries; a player's town is left to the player. On a dedicated server Clan.PlayerClan is the idle
/// clan the world was created around, so every human player's town would be run by the AI, with the player's gold.
/// Troop training at a castle also delivers to MobileParty.MainParty.
///
/// For the handlers below, when the settlement (or clan) belongs to a player, the handler runs inside
/// <see cref="ServerRelay.PlayerScope"/> for that player: Clan.PlayerClan, Hero.MainHero and MobileParty.MainParty
/// answer as that player, so every one of the mod's own checks is right without rewriting any of them. AI settlements
/// run exactly as before. Messages the mod shows the owner during the tick reach that player (LeNoticeComponent).
/// </summary>
internal sealed class LeOwnerScopeComponent : ILeComponent
{
    private const string Owner = "ModderLords.LivingEconomy.OwnerScope";
    private const string B = "BetterEconomy.Behaviors.";

    private static readonly (string Type, string Method, int Params)[] Scoped =
    {
        (B + "CastleEconomyCampaignBehavior", "OnDailyTickSettlement", 1),
        (B + "TownEconomyCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "FeudalEconomyCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "LordInvestmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageDevelopmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageInvestmentCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "VillageInvestmentCampaignBehavior", "OnClanDailyTick", 1),
        (B + "PopulationCampaignBehavior", "OnSettlementDailyTick", 1),
        (B + "CulturalMarketCampaignBehavior", "OnSettlementDailyTick", 1),
        // A caravan arriving in a player's town tells that player about the delivery.
        (B + "CaravanCampaignBehavior", "OnSettlementEntered", 3),
    };

    private static readonly List<MethodInfo> Found = new List<MethodInfo>();
    private static long _scoped;

    public string Id => "owner-scope";

    public string? SkipReason(LeContext context)
    {
        if (!context.IsServer) return "client";
        Found.Clear();
        var missing = new List<string>();
        foreach (var (type, method, count) in Scoped)
        {
            var m = context.Method(type, method, count);
            if (m == null) missing.Add(LivingEconomyLayer.Short(type) + "." + method);
            else Found.Add(m);
        }
        if (Found.Count == 0) return "Living Economy changed; none of its settlement ticks were found";
        if (missing.Count > 0) Log.Warn(LivingEconomyLayer.Tag + "owner-scope: not found (Living Economy changed?): " + LivingEconomyLayer.Some(missing));
        return null;
    }

    public string Install(LeContext context)
    {
        var h = new Harmony(Owner);
        var prefix = new HarmonyMethod(typeof(LeOwnerScopeComponent), nameof(Prefix));
        var finalizer = new HarmonyMethod(typeof(LeOwnerScopeComponent), nameof(Finalizer));
        foreach (var m in Found) h.Patch(m, prefix: prefix, finalizer: finalizer);
        return $"{Found.Count} settlement tick(s) treat a player-owned settlement's owner as the player";
    }

    private static void Prefix(object[] __args, out IDisposable? __state)
    {
        __state = null;
        if (ServerRelay.PlayerScope.Active || __args == null) return;
        Clan? clan = null;
        foreach (var a in __args)
        {
            if (a is Settlement s) { clan = s.OwnerClan; break; }
            if (a is Clan c) { clan = c; break; }
        }
        if (LePlayers.For(clan) is not { } player) return;
        try
        {
            __state = new ServerRelay.PlayerScope(player.Hero, player.Party ?? player.Hero.PartyBelongedTo);
            _scoped++;
        }
        catch (Exception ex) { Log.Warn(LivingEconomyLayer.Tag + "owner-scope could not stand in for " + player.Hero.Name + ": " + ex.GetBaseException().Message); }
    }

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }

    internal static long ScopedCount => _scoped;
}
