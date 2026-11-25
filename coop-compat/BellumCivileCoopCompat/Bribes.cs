using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Settlements;

namespace BellumCivileCoopCompat
{
    // Bellum's vote bribes in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4b): what the player paid.
    //
    // A bribe is a barter on the player's PC: the player offers gold or fiefs for a lord's vote. Bellum's own barterable
    // writes the vote (its setter goes to the server through RemoteCalls); vanilla's barter then pays, but in co-op the
    // PC can't move gold or fiefs (Coop refuses them there), and Coop sends only the barters it knows to its server. So
    // when a Bellum bribe is accepted on the PC, what the player offered (gold, fiefs) is sent too, and the server pays it
    // from the player to the lord.
    public static class Bribes
    {
        private static int _sent, _paid;

        public static string Summary() => _paid > 0 ? "; bribes paid for players " + _paid : _sent > 0 ? "; bribes sent " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var finalize = AccessTools.Method(typeof(BarterManager), "ApplyAndFinalizePlayerBarter");
            if (finalize == null)
            {
                SubModule.Log("Bribes: BarterManager.ApplyAndFinalizePlayerBarter not found; a bribe's gold stays with the player.");
                return;
            }
            harmony.Patch(finalize, postfix: new HarmonyMethod(typeof(Bribes), nameof(FinalizePostfix)));
            Agency.Handle("bribe", OnBribe);
            SubModule.Log("Bribes: a vote bribe's gold and fiefs are paid on the server.");
        }

        // Player's PC: an accepted Bellum bribe sends what the player offered.
        private static void FinalizePostfix(Hero offererHero, Hero otherHero, BarterData barterData)
        {
            if (!Coop.OnPlayerPc || barterData == null || otherHero == null)
                return;
            var bribe = barterData.GetBarterables().FirstOrDefault(b => b != null && b.GetType().Name.EndsWith("VoteBribeBarterable"));
            if (bribe == null || !(AccessTools.Field(bribe.GetType(), "_secured")?.GetValue(bribe) is true))
                return;
            var offered = barterData.GetOfferedBarterables().Where(b => b != null && b != bribe && b.OriginalOwner == offererHero).ToList();
            var gold = offered.OfType<GoldBarterable>().Sum(g => g.CurrentAmount);
            var fiefs = offered.OfType<FiefBarterable>().Select(f => f.TargetSettlement?.StringId).Where(id => id != null).ToList();
            if (gold <= 0 && fiefs.Count == 0)
                return;
            if (Agency.ToServer("bribe", otherHero.StringId, gold.ToString(CultureInfo.InvariantCulture), string.Join(",", fiefs)))
                _sent++;
        }

        // Server: the player pays the lord, as the barter would have.
        private static void OnBribe(Hero sender, string[] args)
        {
            if (args.Length < 3 || sender?.Clan == null)
                return;
            var lord = Hero.FindFirst(h => h.StringId == args[0]);
            if (lord == null || !lord.IsAlive)
            {
                SubModule.Log("Bribes: " + sender.StringId + " paid a lord who is no longer there (" + args[0] + "); nothing paid.");
                return;
            }
            int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gold);
            gold = Math.Max(0, Math.Min(gold, sender.Gold));
            if (gold > 0)
                GiveGoldAction.ApplyBetweenCharacters(sender, lord, gold, false);
            var fiefs = new List<string>();
            foreach (var id in args[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fief = Settlement.Find(id);
                if (fief == null || fief.OwnerClan != sender.Clan) continue;
                ChangeOwnerOfSettlementAction.ApplyByBarter(lord, fief);
                fiefs.Add(id);
            }
            _paid++;
            SubModule.Log("Bribe paid for " + sender.StringId + " to " + lord.StringId + ": " + gold + " gold" + (fiefs.Count > 0 ? ", " + string.Join(", ", fiefs) : "") + ".");
        }
    }
}
