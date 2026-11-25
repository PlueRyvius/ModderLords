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
    // Bellum's barters in co-op (docs/BELLUM-COOP-COVERAGE.md, stages 4b and 4c): what the player agreed and paid.
    //
    // A bribe or an appeasement is a barter on the player's PC: the player offers gold or fiefs for a lord's vote (4b), for
    // his house to join the player's rebel faction, or, as ruler, for a rebel house to leave its faction (4c). Vanilla's
    // barter then pays, but in co-op the PC can't move gold or fiefs (Coop refuses them there), and Coop sends only the
    // barters it knows to its server. So when a Bellum barter is accepted on the PC, it is sent with what the player
    // offered (gold, fiefs), and the server pays it from the player to the lord.
    //  - A vote bribe's pledge is Bellum's setter, which goes to the server through RemoteCalls; only the payment is sent.
    //  - Joining or leaving a faction is the barterable's own Apply. On the PC it is skipped (the faction is the server's,
    //    and arrives through StateMirror); the server finds the faction again from its own state, checks the player may
    //    make that deal, and applies Bellum's barterable as the player.
    public static class Bribes
    {
        private const string JoinType = "BellumCivile.JoinFactionBarterable", LeaveType = "BellumCivile.LeaveFactionBarterable";
        private static int _sent, _paid, _realigned;

        public static string Summary() =>
            (_paid > 0 ? "; bribes paid for players " + _paid : _sent > 0 ? "; bribes sent " + _sent : "")
            + (_realigned > 0 ? "; houses joining or leaving factions for players " + _realigned : "");

        public static void Install(Harmony harmony)
        {
            var finalize = AccessTools.Method(typeof(BarterManager), "ApplyAndFinalizePlayerBarter");
            if (finalize == null)
            {
                SubModule.Log("Bribes: BarterManager.ApplyAndFinalizePlayerBarter not found; a bribe's gold stays with the player.");
                return;
            }
            harmony.Patch(finalize, postfix: new HarmonyMethod(typeof(Bribes), nameof(FinalizePostfix)));
            var skipped = 0;
            foreach (var name in new[] { JoinType, LeaveType })
            {
                var apply = AccessTools.TypeByName(name) is Type type ? AccessTools.Method(type, "Apply") : null;
                if (apply == null) continue;
                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(Bribes), nameof(ApplyPrefix)));
                skipped++;
            }
            Agency.Handle("bribe", OnBribe);
            SubModule.Log("Bribes: a vote bribe's gold and fiefs are paid on the server; " + (skipped == 2
                ? "a house bought into or out of a faction is moved there."
                : "Bellum's faction barters not found (" + skipped + " of 2); they stay on the player's PC."));
        }

        // Player's PC: a faction barter is the server's to apply.
        private static bool ApplyPrefix() => !Coop.OnPlayerPc;

        // Player's PC: an accepted Bellum barter sends what the player agreed and offered.
        private static void FinalizePostfix(Hero offererHero, Hero otherHero, BarterData barterData)
        {
            if (!Coop.OnPlayerPc || barterData == null || otherHero == null)
                return;
            string kind = null;
            Barterable deal = null;
            foreach (var b in barterData.GetOfferedBarterables())
            {
                var name = b?.GetType().FullName;
                if (name == JoinType) kind = "join";
                else if (name == LeaveType) kind = "leave";
                else if (name != null && name.EndsWith("VoteBribeBarterable") && AccessTools.Field(b.GetType(), "_secured")?.GetValue(b) is true) kind = "vote";
                else continue;
                deal = b;
                break;
            }
            if (deal == null)
                return;
            var offered = barterData.GetOfferedBarterables().Where(b => b != null && b != deal && b.OriginalOwner == offererHero).ToList();
            var gold = offered.OfType<GoldBarterable>().Sum(g => g.CurrentAmount);
            var fiefs = offered.OfType<FiefBarterable>().Select(f => f.TargetSettlement?.StringId).Where(id => id != null).ToList();
            if (kind == "vote" && gold <= 0 && fiefs.Count == 0)
                return;
            if (Agency.ToServer("bribe", otherHero.StringId, gold.ToString(CultureInfo.InvariantCulture), string.Join(",", fiefs), kind))
                _sent++;
        }

        // Server: the deal is made as the player, and the player pays the lord, as the barter would have.
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
            var kind = args.Length > 3 ? args[3] : "vote";
            string agreed = null;
            if (kind == "join" || kind == "leave")
            {
                agreed = Realign(sender, lord, kind == "join", out var refusal);
                if (agreed == null)
                {
                    SubModule.Log("Bribes: " + sender.StringId + "'s deal with " + lord.StringId + " (" + kind + ") is refused: " + refusal + "; nothing paid.");
                    Agency.ToPlayer(sender, "notice", refusal);
                    return;
                }
                _realigned++;
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
            SubModule.Log("Bribe paid for " + sender.StringId + " to " + lord.StringId + ": " + gold + " gold" + (fiefs.Count > 0 ? ", " + string.Join(", ", fiefs) : "")
                          + (agreed != null ? "; " + agreed : "") + ".");
            if (agreed != null)
                StateMirror.SendTo(sender); // the faction as it now is
        }

        // Server: lord's house joins the rebel faction sender's house leads, or (sender ruling the realm) leaves its own, as
        // Bellum's AppeasementBehavior offers them. Returns what was done, or null with the reason.
        private static string Realign(Hero sender, Hero lord, bool join, out string refusal)
        {
            refusal = null;
            var manager = RemoteCalls.Behaviour("BellumCivile.Behaviors.FactionManagerBehavior");
            var rebelOf = manager == null ? null : AccessTools.Method(manager.GetType(), "GetRebelFaction", new[] { typeof(Clan) });
            var type = AccessTools.TypeByName(join ? JoinType : LeaveType);
            if (rebelOf == null || type == null) { refusal = "Bellum's factions are not running"; return null; }
            var house = lord.Clan;
            var realm = sender.Clan.Kingdom;
            if (house == null || house == sender.Clan || realm == null || house.Kingdom != realm || house.Leader != lord)
            {
                refusal = lord.Name + " does not speak for a house of your realm";
                return null;
            }
            var faction = rebelOf.Invoke(manager, new object[] { join ? sender.Clan : house });
            var leader = faction == null ? null : AccessTools.Property(faction.GetType(), "Leader")?.GetValue(faction, null) as Clan;
            if (join)
            {
                var members = faction == null ? null : AccessTools.Property(faction.GetType(), "Members")?.GetValue(faction, null) as System.Collections.IEnumerable;
                if (faction == null || leader != sender.Clan) { refusal = "you lead no faction against the crown"; return null; }
                if (house == realm.RulingClan || members?.Cast<object>().Contains(house) == true) { refusal = house.Name + " cannot join your faction"; return null; }
            }
            else
            {
                if (realm.RulingClan != sender.Clan) { refusal = "only the realm's ruler can buy a house back"; return null; }
                if (faction == null) { refusal = house.Name + " is in no faction"; return null; }
            }
            var barterable = (Barterable)Activator.CreateInstance(type, faction, lord, sender);
            barterable.Apply();
            return join ? house.StringId + " joins " + sender.Clan.StringId + "'s faction"
                : leader == house ? house.StringId + "'s faction is disbanded" : house.StringId + " leaves " + leader?.StringId + "'s faction";
        }
    }
}
