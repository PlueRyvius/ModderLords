using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BellumCivileCoopCompat
{
    // Bellum's household ransom in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): a ransom broker frees a captured member
    // of the player's house for a price.
    //
    // The broker's conversation and the list of captives run on the player's PC. The ransom itself is Bellum's
    // HandleHouseholdRansomSelection, run when the player confirms a captive (OnHouseholdRansomConfirmed): it pays the
    // captor and frees the captive, which the PC can't do in co-op. So on the PC the confirmed captive goes to the server, which builds Bellum's own
    // list as that player, finds the captive in it and runs the same method: Bellum's checks, price, payment and release,
    // with what it tells the player sent to them as notices. A treaty hostage (which Bellum refuses with its own text and
    // no action) stays on the PC.
    public static class Ransoms
    {
        private const string BehaviourType = "BellumCivile.Behaviors.HouseholdRansomBehavior";
        private static int _sent, _done;

        public static string Summary() => _done > 0 ? "; ransoms paid for players " + _done : _sent > 0 ? "; ransoms sent " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName(BehaviourType);
            var confirmed = type == null ? null : AccessTools.Method(type, "OnHouseholdRansomConfirmed");
            if (confirmed == null || AccessTools.Method(type, "HandleHouseholdRansomSelection") == null)
            {
                SubModule.Log("Ransoms: Bellum's household ransom not found; it stays off on players' PCs.");
                return;
            }
            harmony.Patch(confirmed, prefix: new HarmonyMethod(typeof(Ransoms), nameof(ConfirmedPrefix)));
            Agency.Handle("ransom", OnRansom);
            SubModule.Log("Ransoms: a household ransom is paid on the server.");
        }

        // Player's PC: the captive confirmed in Bellum's list goes to the server, and the conversation goes on as after a
        // ransom. (The list's Confirm button, not HandleHouseholdRansomSelection: Harmony's prefix there was never called
        // on the PC.)
        private static bool ConfirmedPrefix(object __instance, List<InquiryElement> selected)
        {
            if (!Coop.OnPlayerPc)
                return true;
            var candidate = selected?.FirstOrDefault()?.Identifier;
            var prisoner = Member(candidate, "Prisoner") as Hero;
            if (prisoner == null || Member(candidate, "IsTreatyHostage") is true)
            {
                SubModule.Log("Ransoms: " + (prisoner == null ? "no captive in the choice" : prisoner.StringId + " is a treaty hostage") + "; Bellum answers on the PC.");
                return true;
            }
            var sent = Agency.ToServer("ransom", prisoner.StringId);
            if (sent) _sent++;
            SubModule.Log("Ransoms: the ransom of " + prisoner.StringId + (sent ? " is sent to the server." : " could not be sent to the server."));
            AccessTools.Method(__instance.GetType(), "ResumeBrokerConversation")?.Invoke(null, null);
            return false;
        }

        // Server: Bellum's ransom of that captive, as the player.
        private static void OnRansom(Hero sender, string[] args)
        {
            if (args.Length < 1 || sender == null)
                return;
            var behaviour = RemoteCalls.Behaviour(BehaviourType);
            var build = behaviour == null ? null : AccessTools.Method(behaviour.GetType(), "BuildRansomCandidates");
            var handle = behaviour == null ? null : AccessTools.Method(behaviour.GetType(), "HandleHouseholdRansomSelection");
            if (build == null || handle == null)
            {
                SubModule.Log("Ransom of " + args[0] + " for " + sender.StringId + ": Bellum's household ransom is not running on the server.");
                return;
            }
            var outcome = "not carried out";
            Prompts.RunAsking(sender, "Bellum's household ransom", () =>
            {
                var candidate = (build.Invoke(null, null) as IEnumerable)?.Cast<object>()
                    .FirstOrDefault(c => (Member(c, "Prisoner") as Hero)?.StringId == args[0]);
                if (candidate == null)
                {
                    Agency.ToPlayer(sender, "notice", "The broker can no longer arrange that ransom.");
                    outcome = "no longer offered";
                    return;
                }
                var prisoner = (Hero)Member(candidate, "Prisoner");
                handle.Invoke(behaviour, new object[] { new List<InquiryElement> { new InquiryElement(candidate, prisoner.Name?.ToString() ?? "", null) } });
                outcome = prisoner.IsPrisoner ? "refused" : "done, " + Member(candidate, "RansomPrice") + " gold";
            });
            if (outcome.StartsWith("done")) _done++;
            SubModule.Log("Ransom of " + args[0] + " for " + sender.StringId + ": " + outcome + ".");
        }

        private static object Member(object o, string name) => o == null ? null : AccessTools.Property(o.GetType(), name)?.GetValue(o, null);
    }
}
