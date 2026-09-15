using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server: installs <see cref="PlayerComparisonRewriter"/> on the recipe's PlayerComparisons, answering "is this any
/// player's?" from Coop's own player registry (its public IsPlayerHero / IsPlayerParty extensions).
/// </summary>
public static class ServerPlayerChecks
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.PlayerChecks");

    public static string Apply(string recipesJson)
    {
        var ids = new List<string>();
        try
        {
            foreach (var mod in JObject.Parse(recipesJson)["Mods"] as JArray ?? new JArray())
                ids.AddRange((mod["PlayerComparisons"] as JArray ?? new JArray()).Select(t => t.ToString()));
        }
        catch (Exception ex) { return "recipe parse failed: " + ex.Message; }
        if (ids.Count == 0) return "none in the recipe";

        var t = typeof(ServerPlayerChecks);
        PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(IsAnyPlayerHero))!, t.GetMethod(nameof(IsAnyPlayerClan))!,
            t.GetMethod(nameof(IsAnyPlayerParty))!, t.GetMethod(nameof(IsAnyPlayerPartyBase))!);
        var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(Harmony, ids, msg => Log.Warn(msg));
        return $"{methods} method(s) now ask about any player ({comparisons} comparison(s) rewritten), {missing} not found";
    }

    public static bool IsAnyPlayerHero(Hero? hero)
    {
        if (hero is null) return false;
        try { return hero.IsPlayerHero(); } catch { return false; }
    }

    public static bool IsAnyPlayerClan(Clan? clan)
    {
        if (clan is null) return false;
        try
        {
            foreach (var hero in clan.Heroes)
                if (IsAnyPlayerHero(hero)) return true;
        }
        catch { }
        return false;
    }

    public static bool IsAnyPlayerParty(MobileParty? party)
    {
        if (party is null) return false;
        try { return party.IsPlayerParty(); } catch { return false; }
    }

    public static bool IsAnyPlayerPartyBase(PartyBase? party) => party?.MobileParty is { } mobile && IsAnyPlayerParty(mobile);
}
