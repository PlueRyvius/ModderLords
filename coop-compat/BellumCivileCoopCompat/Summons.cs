using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace BellumCivileCoopCompat
{
    // A player's call to arms in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): a player who leads an army asks a lord
    // of their realm, in conversation, to join it.
    //
    // Bellum decides the lord's answer (ArmySummonsConversationBehavior): under its feudal summons only the player's
    // vassals are bound to come, a friend may come anyway, and a lord whose relations or faction are sour refuses. The
    // conversation runs on the player's PC, where those lines now show. But the answer "we will come", Bellum's or
    // vanilla's, puts the lord's party in the army, which Coop refuses on a PC; and vanilla takes the influence when the
    // player asks, on the PC too. So on the PC the lord who agreed goes to the server, which checks the call again
    // (vanilla's conditions and Bellum's refusal), takes the player's influence and adds his party to the army.
    public static class Summons
    {
        private const string BehaviourType = "BellumCivile.Behaviors.ArmySummonsConversationBehavior";
        private static int _sent, _joined;

        public static string Summary() => _joined > 0 ? "; lords summoned for players " + _joined : _sent > 0 ? "; summons sent " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var bellum = AccessTools.TypeByName(BehaviourType) is Type t ? AccessTools.Method(t, "JoinConversationLordToPlayerArmy") : null;
            var vanilla = AccessTools.Method(typeof(LordConversationsCampaignBehavior), "lord_considers_joining_player_army_on_consequence");
            var ask = AccessTools.Method(typeof(LordConversationsCampaignBehavior), "player_ask_to_join_players_army_on_consequence");
            if (bellum == null || vanilla == null || ask == null)
            {
                SubModule.Log("Summons: a call to arms in conversation not found (Bellum " + (bellum != null) + ", vanilla " + (vanilla != null && ask != null) + "); it stays as it is.");
                return;
            }
            var join = new HarmonyMethod(typeof(Summons), nameof(JoinPrefix));
            harmony.Patch(bellum, prefix: join);
            harmony.Patch(vanilla, prefix: join);
            harmony.Patch(ask, prefix: new HarmonyMethod(typeof(Summons), nameof(AskPrefix)));
            Agency.Handle("summon", OnSummon);
            SubModule.Log("Summons: a lord who agrees to join a player's army is added to it on the server.");
        }

        // Player's PC: the influence is taken on the server, when the lord joins.
        private static bool AskPrefix() => !Coop.OnPlayerPc;

        // Player's PC: the lord who agreed goes to the server.
        private static bool JoinPrefix()
        {
            if (!Coop.OnPlayerPc)
                return true;
            var lord = Hero.OneToOneConversationHero;
            if (lord != null && Agency.ToServer("summon", lord.StringId))
            {
                _sent++;
                SubModule.Log("Summons: " + lord.StringId + " agreed to join the army; sent to the server.");
            }
            return false;
        }

        // Server: the call is checked again as the game and Bellum would for the player, then answered.
        private static void OnSummon(Hero sender, string[] args)
        {
            if (args.Length < 1 || sender == null)
                return;
            var lord = Hero.FindFirst(h => h.StringId == args[0]);
            var refusal = Check(sender, lord, out var party, out var army, out var cost);
            if (refusal != null)
            {
                SubModule.Log("Summons: " + sender.StringId + "'s call to " + args[0] + " is refused: " + refusal + ".");
                Agency.ToPlayer(sender, "notice", refusal);
                return;
            }
            ChangeClanInfluenceAction.Apply(sender.Clan, -cost);
            party.Army = army;
            army.AddPartyToMergedParties(party);
            _joined++;
            SubModule.Log("Summons: " + lord.StringId + " joins " + sender.StringId + "'s army for " + cost + " influence.");
        }

        // Null when lord may join sender's army (vanilla's conditions, Bellum's refusal and the influence it costs), or
        // why not.
        private static string Check(Hero sender, Hero lord, out MobileParty party, out Army army, out int cost)
        {
            party = lord?.PartyBelongedTo;
            var own = sender.PartyBelongedTo;
            army = own?.Army;
            cost = 0;
            if (lord == null || !lord.IsAlive || party == null || !party.IsLordParty || party.LeaderHero != lord)
                return "that lord leads no party now";
            if (army == null || army.LeaderParty != own)
                return "you lead no army";
            if (party.Army != null || party.MapEvent != null || party.SiegeEvent != null)
                return lord.Name + " is busy elsewhere";
            if (lord.MapFaction != sender.MapFaction || lord.MapFaction?.Leader == lord)
                return lord.Name + " does not answer your call";
            if (Refuses(sender, lord))
                return lord.Name + " refuses your call to arms";
            cost = Campaign.Current.Models.ArmyManagementCalculationModel.CalculatePartyInfluenceCost(own, party);
            if (sender.Clan == null || sender.Clan.Influence < cost)
                return "your house lacks the " + cost + " influence it costs";
            return null;
        }

        // Bellum's answer for the player: a refusal stands unless the lord comes as the player's friend.
        private static bool Refuses(Hero sender, Hero lord)
        {
            var helper = AccessTools.TypeByName("BellumCivile.ArmySummonsEligibilityHelper");
            var refuse = helper == null ? null : AccessTools.Method(helper, "ShouldRefuseCallToArms");
            var friend = helper == null ? null : AccessTools.Method(helper, "CanAnswerAsPersonalFriend");
            if (refuse == null || friend == null)
                return false;
            var args = new object[] { sender.Clan, lord.Clan, null, null, null };
            if (!(refuse.Invoke(null, args) is true))
                return false;
            return !(friend.Invoke(null, new[] { sender, lord, args[2] }) is true);
        }
    }
}
