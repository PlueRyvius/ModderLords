using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Players' PCs: Bellum founds no kingdoms, and a kingdom founded there anyway is taken back out.
    //
    // Kingdoms are the server's to make: Coop creates each one on every PC and gives it its ID.
    // Bellum's own copy of a rebellion or feud on a player's PC calls Kingdom.CreateKingdom there;
    // Coop refuses every write after that (ID, name, banner, ruling clan), but the empty kingdom is
    // already in the campaign's list. Reported 2026-10-11: the Kingdom screen opened black, and
    // Bellum's daily audit of ruling clans threw on every tick from then on.
    internal static class ClientKingdoms
    {
        /// <summary>Every kingdom Bellum founds (rebel, breakaway, restored, feud and granted realms) goes through this.</summary>
        public const string Maker = "BellumCivile.KingdomCreationSafetyHelper";
        public const string MakerMethod = "CreateKingdom";

        private static bool _told, _shapeTold;

        public static void Install(Harmony harmony)
        {
            var maker = AccessTools.TypeByName(Maker);
            var target = maker == null ? null : AccessTools.Method(maker, MakerMethod);
            if (target == null || target.ReturnType != typeof(Kingdom))
            {
                SubModule.Log("Bellum makes kingdoms some other way in this version; a player's PC may still found one (the sweep still removes it).");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ClientKingdoms), nameof(Refuse)) { priority = Priority.First });
            SubModule.Log("Kingdoms: on a player's PC Bellum founds none; the server's arrive through Coop.");
        }

        // CreateKingdom returns null when it refuses, and every caller handles that.
        private static bool Refuse(ref Kingdom __result, string __0)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = null;
            if (!_told)
            {
                _told = true;
                SubModule.Log("Bellum tried to found '" + __0 + "' on this player's PC; refused (said once).");
            }
            return false;
        }

        // Coop gives every kingdom it creates an ID and loads the rest from the save with theirs, so
        // a kingdom without one was made by this PC's own code and exists nowhere else.
        public static void Tick()
        {
            var objects = Campaign.Current?.CampaignObjectManager;
            if (objects == null)
                return;
            List<Kingdom> empty = null;
            foreach (var kingdom in objects.Kingdoms)
                if (kingdom != null && string.IsNullOrEmpty(kingdom.StringId))
                    (empty = empty ?? new List<Kingdom>()).Add(kingdom);
            if (empty == null)
                return;

            var kingdoms = AccessTools.Field(objects.GetType(), "_kingdoms")?.GetValue(objects) as IList;
            var factions = AccessTools.Field(objects.GetType(), "_factions")?.GetValue(objects) as IList;
            if (kingdoms == null)
            {
                if (!_shapeTold)
                    SubModule.Log("The campaign's kingdom list has changed shape; a kingdom with no ID stays in it.");
                _shapeTold = true;
                return;
            }
            foreach (var kingdom in empty)
            {
                kingdoms.Remove(kingdom);
                factions?.Remove(kingdom);
                SubModule.Log("Removed a kingdom with no ID from this player's PC (name " + (kingdom.Name?.ToString() ?? "none")
                    + ", ruling clan " + (kingdom.RulingClan?.StringId ?? "none") + "). It existed only here.");
            }
        }
    }
}
