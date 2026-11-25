using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace BellumCivileCoopCompat
{
    // A companion made a vassal house in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): a player vassal who holds a
    // barony and a title above it grants the barony to a companion, who founds a new house under the player
    // (CompanionSubinfeudationBehavior).
    //
    // The conversation, the list of baronies and the two confirmations run on the player's PC. The grant itself is
    // Bellum's TryCreateVassalClan, run when the player names the new house (CompleteGrant): it creates a clan and new
    // heroes, moves the barony and its title, and pays the companion, none of which a PC may do in co-op. So on the PC the
    // companion, the barony and the name go to the server, which builds Bellum's preview of that grant again as the player
    // and runs TryCreateVassalClan there; what Bellum tells the player reaches them as notices. The PC's conversation goes
    // on as after a grant.
    public static class Subinfeudation
    {
        private const string BehaviourType = "BellumCivile.Behaviors.CompanionSubinfeudationBehavior";
        private const string ServiceType = "BellumCivile.Behaviors.CompanionSubinfeudationService";
        private static int _sent, _done;

        public static string Summary() => _done > 0 ? "; vassal houses founded for players " + _done : _sent > 0 ? "; vassal houses sent " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName(BehaviourType);
            var complete = type == null ? null : AccessTools.Method(type, "CompleteGrant");
            var service = AccessTools.TypeByName(ServiceType);
            if (complete == null || service == null || AccessTools.Method(service, "TryBuildPreview") == null || AccessTools.Method(service, "TryCreateVassalClan") == null)
            {
                SubModule.Log("Subinfeudation: Bellum's companion vassal houses not found; they stay off on players' PCs.");
                return;
            }
            harmony.Patch(complete, prefix: new HarmonyMethod(typeof(Subinfeudation), nameof(CompletePrefix)));
            Agency.Handle("enfeoff", OnEnfeoff);
            SubModule.Log("Subinfeudation: a companion's new vassal house is founded on the server.");
        }

        // Player's PC: the companion, the barony and the house's name go to the server, and the conversation goes on as
        // after a grant.
        private static bool CompletePrefix(object __instance, string clanName)
        {
            if (!Coop.OnPlayerPc)
                return true;
            var companion = Hero.OneToOneConversationHero;
            var settlement = Field(Field(__instance, "_selectedGrant"), "Settlement") as Settlement;
            var sent = companion != null && settlement != null && Agency.ToServer("enfeoff", companion.StringId, settlement.StringId, clanName ?? "");
            if (sent) _sent++;
            SubModule.Log("Subinfeudation: " + (companion?.StringId ?? "no companion") + "'s house at " + (settlement?.StringId ?? "no barony")
                + (sent ? " sent to the server." : " could not be sent to the server."));
            AccessTools.Field(__instance.GetType(), "_grantCompleted")?.SetValue(__instance, sent);
            Campaign.Current?.ConversationManager?.ContinueConversation();
            return false;
        }

        // Server: Bellum's grant, as the player.
        private static void OnEnfeoff(Hero sender, string[] args)
        {
            if (args.Length < 3 || sender == null)
                return;
            var companion = Hero.FindFirst(h => h.StringId == args[0]);
            var settlement = Settlement.Find(args[1]);
            var service = AccessTools.TypeByName(ServiceType);
            var outcome = "not carried out";
            if (companion == null || settlement == null)
                outcome = "no such companion or barony";
            else
                Prompts.RunAsking(sender, "Bellum's companion vassal house", () =>
                {
                    var preview = new object[] { companion, settlement, null, null };
                    if (!(AccessTools.Method(service, "TryBuildPreview").Invoke(null, preview) is true))
                    {
                        outcome = "refused: " + preview[3];
                        Agency.ToPlayer(sender, "notice", preview[3]?.ToString() ?? "The grant can no longer be made.");
                        return;
                    }
                    var grant = new object[] { companion, preview[2], args[2], null, null };
                    if (AccessTools.Method(service, "TryCreateVassalClan").Invoke(null, grant) is true && grant[3] is Clan clan)
                        outcome = "done, " + clan.StringId + " \"" + clan.Name + "\"";
                    else
                    {
                        outcome = "refused: " + grant[4];
                        Agency.ToPlayer(sender, "notice", (grant[4] as TextObject)?.ToString() ?? "The new house could not be established.");
                    }
                });
            if (outcome.StartsWith("done")) _done++;
            SubModule.Log("Subinfeudation: " + sender.StringId + "'s grant of " + args[1] + " to " + args[0] + ": " + outcome + ".");
        }

        private static object Field(object o, string name) =>
            o == null ? null : AccessTools.Field(o.GetType(), name)?.GetValue(o) ?? AccessTools.Property(o.GetType(), name)?.GetValue(o, null);
    }
}
