using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// On a co-op player's game, Bellum Civile founds no kingdoms, and a kingdom that was founded there anyway is taken
/// back out of the campaign.
///
/// <para>Kingdoms are the server's to make: Coop creates each one on every player's game and gives it its ID. Bellum
/// runs its politics on a player's game too, and when its own copy of a rebellion or feud reaches the point of
/// founding a realm it calls <c>Kingdom.CreateKingdom</c> there. Coop refuses a player's game every write that
/// follows (the ID, the name, the banner, the ruling clan), but the empty kingdom is already in the campaign's
/// list.</para>
///
/// <para>Reported 2026-10-11 by a host playing on their own server. The Kingdom screen opened black: its diplomacy
/// list builds a row for every kingdom at peace with the player's, and one row threw
/// (<c>KingdomDiplomacyItemVM.UpdateDiplomacyProperties</c>). Bellum's daily audit of ruling clans threw on a
/// kingdom with no ID on every campaign tick from then on, so nothing after it in the daily tick ran on that
/// player's game. The server's Bellum had split a realm into two feud kingdoms a few minutes earlier. The report's
/// log was cut across those minutes, so which of Bellum's founding paths ran on the player's game is not known;
/// a kingdom with no ID can only have been made there, because Coop sets the ID on every kingdom it creates.</para>
///
/// <para>The server's Bellum is untouched. So is Bellum on its own.</para>
/// </summary>
public static class BellumClientKingdoms
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumClientKingdoms");
    private static bool _installTried, _told, _shapeTold;


    /// <summary>A player's game in a co-op session.</summary>
    private static bool Active => !Operations.OperationProcessSide.IsServer && TaomActions.IsCoopClient;

    /// <summary>Patches Bellum's kingdom maker once Bellum is loaded. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (Operations.OperationProcessSide.IsServer) return;
        var maker = AccessTools.TypeByName(BellumClientKingdomTargets.Maker);
        if (maker == null) return;   // Bellum is not loaded, or has moved it; the sweep below still runs
        try
        {
            var target = AccessTools.Method(maker, BellumClientKingdomTargets.MakerMethod);
            if (target == null || target.ReturnType != typeof(Kingdom))
            {
                Log.Warn("Bellum kingdoms in co-op: this Bellum version makes kingdoms some other way; a player's game may still found one of its own");
                return;
            }
            Harmony.Patch(target, prefix: new HarmonyMethod(typeof(BellumClientKingdoms), nameof(Refuse)) { priority = Priority.First });
            Log.Info("Bellum kingdoms in co-op: on a player's game Bellum founds no kingdoms; the server's arrive through Coop");
        }
        catch (Exception ex) { Log.Warn("Bellum kingdoms in co-op not installed: " + ex.GetBaseException().Message); }
    }

    private static bool Refuse(ref Kingdom? __result, string __0)
    {
        if (!Active) return true;
        __result = null;
        if (!_told)
        {
            _told = true;
            Log.Info($"Bellum kingdoms in co-op: Bellum tried to found '{__0}' on this player's game; refused (said once)");
        }
        return false;
    }

    /// <summary>
    /// Every 3 s on a player's game: takes kingdoms with no ID out of the campaign. Coop gives every kingdom it
    /// creates an ID and loads the rest from the save with theirs, so a kingdom without one was made by this game's
    /// own code and exists nowhere else.
    /// </summary>
    public static void ClientTick()
    {
        if (!Active) return;
        var objects = Campaign.Current?.CampaignObjectManager;
        if (objects == null) return;
        List<Kingdom>? empty = null;
        foreach (var kingdom in objects.Kingdoms)
            if (kingdom != null && string.IsNullOrEmpty(kingdom.StringId)) (empty ??= new List<Kingdom>()).Add(kingdom);
        if (empty == null) return;

        var kingdoms = AccessTools.Field(objects.GetType(), "_kingdoms")?.GetValue(objects) as IList;
        var factions = AccessTools.Field(objects.GetType(), "_factions")?.GetValue(objects) as IList;
        if (kingdoms == null)
        {
            if (!_shapeTold) Log.Warn("Bellum kingdoms in co-op: the campaign's kingdom list has changed shape; a kingdom with no ID stays in it");
            _shapeTold = true;
            return;
        }
        foreach (var kingdom in empty)
        {
            kingdoms.Remove(kingdom);
            factions?.Remove(kingdom);
            Log.Warn("Bellum kingdoms in co-op: removed a kingdom with no ID from this player's game (name "
                + (kingdom.Name?.ToString() ?? "none") + ", ruling clan " + (kingdom.RulingClan?.StringId ?? "none")
                + "). It existed only here; the Kingdom screen and the daily tick fail on one.");
        }
    }
}
