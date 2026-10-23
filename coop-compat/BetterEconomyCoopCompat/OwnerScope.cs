using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BetterEconomyCoopCompat;

/// <summary>
/// "The player" inside a settlement's daily tick is that settlement's owner, when a player owns it
/// .
///
/// Living Economy decides a lot per settlement by asking whether its owner clan is Clan.PlayerClan: AI towns pick their
/// own tax policy, projects, estate actions and armory upgrades, AI lords pour their gold into their towns, villages
/// and castle treasuries; a player's town is left to the player. On a dedicated server Clan.PlayerClan is the idle
/// clan the world was created around, so every human player's town would be run by the AI, with the player's gold.
/// Troop training at a castle also delivers to MobileParty.MainParty.
///
/// For the handlers below, when the settlement (or clan) belongs to a player, the handler runs inside
/// <see cref="PlayerScope"/> for that player: Clan.PlayerClan, Hero.MainHero and MobileParty.MainParty
/// answer as that player, so every one of the mod's own checks is right without rewriting any of them. AI settlements
/// run exactly as before. Messages the mod shows the owner during the tick reach that player (Notices).
/// </summary>
internal sealed class OwnerScope : IComponent
{
    private const string Owner = "modderlords.bettereconomy.coopcompat.OwnerScope";
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

    public string? SkipReason(Context context)
    {
        if (!context.IsServer) return "client";
        Found.Clear();
        var missing = new List<string>();
        foreach (var (type, method, count) in Scoped)
        {
            var m = context.Method(type, method, count);
            if (m == null) missing.Add(Layer.Short(type) + "." + method);
            else Found.Add(m);
        }
        if (Found.Count == 0) return "Living Economy changed; none of its settlement ticks were found";
        if (missing.Count > 0) Log.Warn("owner-scope: not found (Living Economy changed?): " + Layer.Some(missing));
        return null;
    }

    public string Install(Context context)
    {
        var h = new Harmony(Owner);
        var prefix = new HarmonyMethod(typeof(OwnerScope), nameof(Prefix));
        var finalizer = new HarmonyMethod(typeof(OwnerScope), nameof(Finalizer));
        foreach (var m in Found) h.Patch(m, prefix: prefix, finalizer: finalizer);
        return $"{Found.Count} settlement tick(s) treat a player-owned settlement's owner as the player";
    }

    private static void Prefix(object[] __args, out IDisposable? __state)
    {
        __state = null;
        if (PlayerScope.Active || __args == null || !Layer.IsCoopServer) return;
        Clan? clan = null;
        foreach (var a in __args)
        {
            if (a is Settlement s) { clan = s.OwnerClan; break; }
            if (a is Clan c) { clan = c; break; }
        }
        if (Players.For(clan) is not { } player) return;
        try
        {
            __state = new PlayerScope(player, player.PartyBelongedTo);
            _scoped++;
        }
        catch (Exception ex) { Log.Warn("owner-scope could not stand in for " + player.Name + ": " + ex.GetBaseException().Message); }
    }

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }

    internal static long ScopedCount => _scoped;
}
