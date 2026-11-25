using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Party;

namespace BellumCivileCoopCompat
{
    // A marriage proposed in conversation in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): a player picks a member of
    // their house and one of a lord's, and the house the couple will belong to, then bargains over the marriage
    // (SpecificMarriageProposalBehavior).
    //
    // The conversation and the barter run on the player's PC. Bellum ties its terms (the couple, the chosen house and its
    // price) to the barter's MarriageBarterable there. Coop sends an accepted marriage barter to its server, which builds a
    // new MarriageBarterable for the same couple and applies it as that player. That new one had no terms, so Bellum gave
    // it its default ones: the house the couple joined was not the one agreed, and the price was not the one the PC
    // bargained over. So when the barter opens, the PC sends the terms to the server. When Coop's server builds its
    // MarriageBarterable for that player and couple, the terms are bound to it. Bellum then prices and checks it as on
    // the PC, and the wedding puts the couple in the agreed house.
    //
    // A companion may marry as a member of a house only where Bellum keeps households (HouseholdMembership.Supports): a
    // lords' house, or the player's own. On the server "the player's own" is Coop's placeholder, so a player's companion was
    // no one's to marry there, and Coop refused the barter ("no longer eligible"). On the server every player's house
    // counts as the player's own for this.
    public static class Marriages
    {
        private const string BehaviourType = "BellumCivile.Behaviors.SpecificMarriageProposalBehavior";
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
        private static readonly Dictionary<Hero, Terms> Pending = new Dictionary<Hero, Terms>();
        private static int _sent, _bound, _houses;
        [ThreadStatic] private static bool _binding;

        private sealed class Terms
        {
            public Hero Player, Other;
            public Clan Destination;
            public DateTime Until;
        }

        public static string Summary() => (_bound > 0 ? "; marriage terms kept for players " + _bound : _sent > 0 ? "; marriage terms sent " + _sent : "")
            + (_houses > 0 ? "; players' households counted " + _houses : "");

        public static void Install(Harmony harmony)
        {
            var behaviour = AccessTools.TypeByName(BehaviourType);
            var open = behaviour == null ? null : AccessTools.Method(behaviour, "OpenBarter");
            var ctor = AccessTools.Constructor(typeof(MarriageBarterable), new[] { typeof(Hero), typeof(PartyBase), typeof(Hero), typeof(Hero) });
            if (open == null || ctor == null || Bellum.Create == null || Bellum.Bind == null)
            {
                SubModule.Log("Marriages: Bellum's marriage proposal not found; its lines stay off on players' PCs.");
                return;
            }
            harmony.Patch(open, prefix: new HarmonyMethod(typeof(Marriages), nameof(OpenPrefix)));
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(Marriages), nameof(BuiltPostfix)));
            var supports = AccessTools.TypeByName("BellumCivile.HouseholdMembership") is Type membership ? AccessTools.Method(membership, "Supports", new[] { typeof(Clan) }) : null;
            if (supports != null)
                harmony.Patch(supports, postfix: new HarmonyMethod(typeof(Marriages), nameof(SupportsPostfix)));
            else
                SubModule.Log("Marriages: Bellum's HouseholdMembership.Supports not found; a player's companion may not marry on the server.");
            var value = AccessTools.Method(typeof(MarriageBarterable), "GetUnitValueForFaction");
            if (value != null)
                harmony.Patch(value, postfix: new HarmonyMethod(typeof(Marriages), nameof(ValuePostfix)) { priority = Priority.Last });
            Agency.Handle("marriage", OnTerms);
            SubModule.Log("Marriages: a player's marriage terms are kept when the server concludes the marriage.");
        }

        // Both sides: what the marriage is worth to the other house, in the log once per barter (the PC and the server
        // must agree on it).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MarriageBarterable, object> Logged = new System.Runtime.CompilerServices.ConditionalWeakTable<MarriageBarterable, object>();

        private static void ValuePostfix(MarriageBarterable __instance, IFaction faction, int __result)
        {
            if (__instance == null || Logged.TryGetValue(__instance, out _) || !(Coop.OnServer || Coop.OnPlayerPc))
                return;
            if (__instance.ProposingHero?.Clan == null || (faction != __instance.ProposingHero.Clan && faction != __instance.HeroBeingProposedTo?.Clan))
                return;
            if (Coop.OnServer && !Coop.IsPlayerHero(__instance.OriginalOwner) && !Bellum.Reading())
                return;
            Logged.Add(__instance, null);
            SubModule.Log("Marriages: " + __instance.HeroBeingProposedTo?.StringId + " and " + __instance.ProposingHero.StringId + (Bellum.Reading() ? " natively" : "") + " are worth " + __result + " to " + faction.StringId
                + " (as " + Hero.MainHero?.StringId + ", in " + Clan.PlayerClan?.StringId + ").");
        }

        // Server: a player's house keeps a household, as the player's own does in single player.
        private static void SupportsPostfix(Clan clan, ref bool __result)
        {
            if (__result || clan == null || !Coop.OnServer || clan.IsEliminated || clan.IsBanditFaction || clan.Leader == null || !clan.Leader.IsAlive || !Coop.IsPlayerClan(clan))
                return;
            __result = true;
            _houses++;
        }

        // Player's PC: the barter opens; the terms go to the server first.
        private static void OpenPrefix(object agreement)
        {
            if (!Coop.OnPlayerPc || agreement == null)
                return;
            var player = Field(agreement, "Player") as Hero;
            var other = Field(agreement, "Other") as Hero;
            var destination = Field(agreement, "Destination") as Clan;
            if (player == null || other == null || destination == null)
                return;
            if (Agency.ToServer("marriage", player.StringId, other.StringId, destination.StringId))
            {
                _sent++;
                SubModule.Log("Marriages: " + player.StringId + " and " + other.StringId + " in " + destination.StringId + " sent to the server.");
            }
        }

        // Server: a player's terms, kept until Coop builds that player's marriage barter.
        private static void OnTerms(Hero sender, string[] args)
        {
            if (args.Length < 3 || sender == null)
                return;
            var player = Hero.FindFirst(h => h.StringId == args[0]);
            var other = Hero.FindFirst(h => h.StringId == args[1]);
            var destination = Clan.FindFirst(c => c.StringId == args[2]);
            string outcome;
            if (player == null || other == null || destination == null)
                outcome = "refused: no such hero or house";
            else if (player.Clan == null || player.Clan != sender.Clan)
                outcome = "refused: " + player.StringId + " is not of the player's house";
            else if (destination != player.Clan && destination != other.Clan)
                outcome = "refused: " + destination.StringId + " is neither of their houses";
            else
            {
                Pending[sender] = new Terms { Player = player, Other = other, Destination = destination, Until = DateTime.UtcNow + Lifetime };
                outcome = "kept";
            }
            SubModule.Log("Marriages: " + sender.StringId + "'s terms for " + args[0] + " and " + args[1] + " in " + args[2] + ": " + outcome + ".");
        }

        // Server: Coop builds the player's marriage barter; the player's terms are bound to it before Bellum reads it.
        private static void BuiltPostfix(MarriageBarterable __instance, Hero owner, Hero heroBeingProposedTo, Hero proposingHero)
        {
            if (_binding || owner == null || !Coop.OnServer || !Pending.TryGetValue(owner, out var terms))
                return;
            if (terms.Until < DateTime.UtcNow)
            {
                Pending.Remove(owner);
                return;
            }
            var pair = (terms.Player == heroBeingProposedTo && terms.Other == proposingHero) || (terms.Player == proposingHero && terms.Other == heroBeingProposedTo);
            if (!pair || Bellum.Reading())
                return;
            Pending.Remove(owner);
            _binding = true;
            try
            {
                var agreement = Bellum.Create.Invoke(null, new object[] { terms.Player, terms.Other, terms.Destination, true });
                var bound = Bellum.Bind.Invoke(null, new object[] { __instance, agreement, null }) is true;
                if (bound) _bound++;
                SubModule.Log("Marriages: " + owner.StringId + "'s marriage of " + terms.Player.StringId + " and " + terms.Other.StringId + " concluded in "
                    + terms.Destination.StringId + (bound ? "." : ": Bellum did not take the terms."));
            }
            catch (Exception ex)
            {
                SubModule.Log("Marriages: binding " + owner.StringId + "'s terms threw: " + ex.GetBaseException().Message);
            }
            finally { _binding = false; }
        }

        private static object Field(object o, string name) => o == null ? null : AccessTools.Field(o.GetType(), name)?.GetValue(o);

        // Bellum's members, looked up once.
        private static class Bellum
        {
            private static readonly Type Agreement = AccessTools.TypeByName("BellumCivile.PlayerMarriageAgreement");
            private static readonly Type Pricing = AccessTools.TypeByName("BellumCivile.PlayerMarriagePricing");
            private static readonly System.Reflection.FieldInfo ReadingNative = Pricing == null ? null : AccessTools.Field(Pricing, "ReadingNative");
            public static readonly System.Reflection.MethodInfo Create = Agreement == null ? null : AccessTools.Method(Agreement, "Create");
            public static readonly System.Reflection.MethodInfo Bind = Pricing == null ? null : AccessTools.Method(Pricing, "Bind");

            // Bellum builds a MarriageBarterable of its own while pricing one (to read the game's own value); that one
            // stays as it is.
            public static bool Reading() => ReadingNative?.GetValue(null) is true;
        }
    }
}
