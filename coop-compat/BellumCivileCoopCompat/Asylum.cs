using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BellumCivileCoopCompat
{
    // Asylum for an exiled house in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): an exiled lord asks a player ruler for
    // refuge, waits in the player's keep, and in conversation swears homage, is refused, or is promised an estate
    // (ExiledClanRecoveryBehavior).
    //
    // Bellum asks only "the player" (Clan.PlayerClan, Hero.MainHero), and only from its map screen. On a dedicated server
    // "the player" is Coop's placeholder and there is no map screen, so an exile bound for a player's realm just joined it,
    // unasked. On the server now:
    //  - A realm a player rules is a player's refuge (IsPlayerRuledValidRefuge, TryMoveClanToRefuge's ruling-clan test).
    //  - The request, the visit (the exile waits in the player's keep) and the admission run as that player, and a visit
    //    is that player's to answer (AsylumAuthorityValid).
    //  - Bellum's letter goes to that player as a notice instead of a map-screen window.
    // The conversation runs on the player's PC, from the server's visits (StateMirror). The homage, an estate promised and
    // a refusal go to the server, which carries them out as the player after checking what Bellum checks; the PC's
    // conversation goes on as after them.
    public static class Asylum
    {
        private const string BehaviourType = "BellumCivile.Behaviors.ExiledClanRecoveryBehavior";
        private static readonly HashSet<string> Sworn = new HashSet<string>();
        private static int _asked, _sent, _done;

        public static string Summary() =>
            (_asked > 0 ? "; asylum letters to players " + _asked : "") + (_done > 0 ? "; asylum answers for players " + _done : _sent > 0 ? "; asylum answers sent " + _sent : "");

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName(BehaviourType);
            if (type == null || B.Visits == null || B.ConversationVisit == null || B.Admission == null || B.Relation == null || B.ValidRefuge == null)
            {
                SubModule.Log("Asylum: Bellum's asylum audiences not found (" + string.Join(", ", B.Missing()) + "); they stay off for players.");
                return;
            }
            var missing = new List<string>();
            void Patch(string name, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
            {
                var method = AccessTools.Method(type, name);
                if (method == null) { missing.Add(name); return; }
                harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Asylum), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Asylum), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Asylum), finalizer),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(Asylum), transpiler));
            }
            // Server: which realm is a player's refuge, and whose visit it is.
            Patch("IsPlayerRuledValidRefuge", postfix: nameof(RefugePostfix));
            Patch("AsylumAuthorityValid", prefix: nameof(AuthorityPrefix));
            Patch("TryMoveClanToRefuge", transpiler: nameof(RefugeTranspiler));
            Patch("CanPresentAsylumNotice", prefix: nameof(NoticePrefix));
            Patch("ShowPlayerAsylumInquiry", prefix: nameof(LetterPrefix));
            // Server: as the ruling player.
            Patch("QueuePlayerAsylumRequest", prefix: nameof(QueuePrefix), finalizer: nameof(Leave));
            Patch("TryCreateAsylumVisit", prefix: nameof(CreatePrefix), finalizer: nameof(Leave));
            Patch("CompleteAsylumAdmission", prefix: nameof(AdmissionPrefix), finalizer: nameof(Leave));
            // Player's PC: the answers go to the server.
            Patch("CompleteAsylumOath", prefix: nameof(OathPrefix));
            Patch("CompleteAsylumEstate", prefix: nameof(EstatePrefix));
            Patch("RefuseAsylumAudience", prefix: nameof(RefusePrefix));
            Patch("CanConcludeAsylum", postfix: nameof(ConcludePostfix));
            Patch("BeginAsylumConversation", postfix: nameof(BeginPostfix));
            Agency.Handle("asylum", OnAnswer);
            SubModule.Log("Asylum: an exile's request reaches the player ruling the refuge, and the player's answer is carried out on the server"
                + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : "") + ".");
        }

        // ---- server: whose refuge, whose visit ----

        private static void RefugePostfix(Kingdom kingdom, ref bool __result)
        {
            if (__result || !Coop.OnServer || kingdom?.RulingClan == null || !Coop.IsPlayerClan(kingdom.RulingClan))
                return;
            __result = B.ValidRefuge.Invoke(null, new object[] { kingdom }) is true;
        }

        private static bool AuthorityPrefix(object visit, ref bool __result)
        {
            if (!Coop.OnServer)
                return true;
            var ruler = Get(visit, "Ruler") as Hero;
            var realm = Get(visit, "Realm") as Kingdom;
            __result = ruler != null && ruler.IsAlive && !ruler.IsDisabled && Coop.IsPlayerHero(ruler) && realm?.Leader == ruler && realm.RulingClan == ruler.Clan
                && B.IsRefuge(realm);
            return false;
        }

        // TryMoveClanToRefuge: refuge.RulingClan == Clan.PlayerClan  ->  refuge.RulingClan == RulingPlayer(refuge.RulingClan)
        private static IEnumerable<CodeInstruction> RefugeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var playerClan = AccessTools.PropertyGetter(typeof(Clan), nameof(Clan.PlayerClan));
            CodeInstruction previous = null;
            var rewrites = 0;
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == playerClan && previous?.operand is MethodInfo before && before.Name == "get_RulingClan")
                {
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.labels.AddRange(code.labels);
                    dup.blocks.AddRange(code.blocks);
                    yield return dup;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Asylum), nameof(RulingPlayer)));
                    previous = null;
                    rewrites++;
                    continue;
                }
                previous = code;
                yield return code;
            }
            if (rewrites != 1)
                SubModule.Log("Asylum: TryMoveClanToRefuge has changed (" + rewrites + " ruling-clan tests); exiles still join players' realms unasked.");
        }

        /// <summary>The refuge's ruling clan itself when a player leads it (on the server), else the game's player clan.</summary>
        private static Clan RulingPlayer(Clan ruling) => Coop.OnServer && ruling != null && Coop.IsPlayerClan(ruling) ? ruling : Clan.PlayerClan;

        // A dedicated server has no map screen; its letters go to players instead.
        private static bool NoticePrefix(ref bool __result)
        {
            if (!Coop.OnServer)
                return true;
            __result = Campaign.Current != null;
            return false;
        }

        // Server: Bellum's letter, as a notice to the ruling player.
        private static bool LetterPrefix(object __instance, Clan clan, Kingdom targetKingdom)
        {
            if (!Coop.OnServer)
                return true;
            if (clan == null || !(B.Visits.GetValue(__instance) is IDictionary visits) || !(visits[clan.StringId] is object visit) || Get(visit, "Notified") is true)
                return false;
            Set(visit, "Notified", true);
            var ruler = Get(visit, "Ruler") as Hero;
            var settlement = Get(visit, "Settlement") as Settlement;
            var deadline = Get(visit, "Deadline") is CampaignTime d ? d : CampaignTime.Now;
            var days = Math.Max(1, (int)Math.Ceiling(deadline.RemainingDaysFromNow));
            var text = (clan.Leader?.Name?.ToString() ?? clan.Name.ToString()) + " of the " + clan.Name + ", in exile, asks for refuge in " + targetKingdom?.Name
                + ". " + (clan.Leader != null && clan.Leader.IsFemale ? "She" : "He") + " awaits your answer in the keep of " + settlement?.Name + " for " + days + " days.";
            if (ruler != null && Agency.ToPlayer(ruler, "notice", text))
                _asked++;
            SubModule.Log("Asylum: " + clan.StringId + " asks " + ruler?.StringId + " for refuge in " + targetKingdom?.StringId + ", waiting at " + settlement?.StringId + ".");
            return false;
        }

        private static void QueuePrefix(Kingdom targetKingdom, out IDisposable __state) => __state = Coop.OnServer && Coop.IsPlayerHero(targetKingdom?.Leader) ? AsPlayer.Enter(targetKingdom.Leader) : null;

        private static void CreatePrefix(Kingdom realm, out IDisposable __state) => __state = Coop.OnServer && Coop.IsPlayerHero(realm?.Leader) ? AsPlayer.Enter(realm.Leader) : null;

        private static void AdmissionPrefix(object visit, out IDisposable __state) =>
            __state = Coop.OnServer && Get(visit, "Ruler") is Hero ruler && Coop.IsPlayerHero(ruler) ? AsPlayer.Enter(ruler) : null;

        private static Exception Leave(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();
            return __exception;
        }

        // ---- player's PC: the answers ----

        private static void BeginPostfix() => Sworn.Clear();

        // The homage: sent; the conversation goes on as after an admission.
        private static bool OathPrefix(object __instance)
        {
            if (!Coop.OnPlayerPc)
                return true;
            var visit = B.ConversationVisit.Invoke(__instance, null);
            if (!(B.OathSworn.GetValue(__instance) is true) || visit == null || visit != B.Conversation.GetValue(__instance) || !(Get(visit, "Clan") is Clan clan))
                return false;
            if (Agency.ToServer("asylum", clan.StringId, "oath"))
            {
                _sent++;
                Sworn.Add(clan.StringId);
                Set(visit, "State", 1);
                B.Admitted.SetValue(__instance, true);
                SubModule.Log("Asylum: " + clan.StringId + "'s homage sent to the server.");
            }
            return false;
        }

        // The estate promised: sent with the village.
        private static bool EstatePrefix(object __instance)
        {
            if (!Coop.OnPlayerPc)
                return true;
            B.Granted.SetValue(__instance, false);
            var visit = B.Conversation.GetValue(__instance);
            var village = B.Offered.GetValue(__instance) as Settlement;
            if (!(Get(visit, "Clan") is Clan clan) || village == null || !Sworn.Contains(clan.StringId) || !(B.EstateAvailable.Invoke(__instance, null) is true))
                return false;
            if (Agency.ToServer("asylum", clan.StringId, "estate", village.StringId))
            {
                _sent++;
                Set(visit, "PromisedVillage", village);
                B.Granted.SetValue(__instance, true);
                SubModule.Log("Asylum: " + clan.StringId + "'s estate " + village.StringId + " sent to the server.");
            }
            return false;
        }

        private static bool RefusePrefix(object __instance)
        {
            if (!Coop.OnPlayerPc)
                return true;
            var visit = B.ConversationVisit.Invoke(__instance, null);
            if (Get(visit, "Clan") is Clan clan && Agency.ToServer("asylum", clan.StringId, "refuse"))
            {
                _sent++;
                Set(visit, "State", 2);
                SubModule.Log("Asylum: " + clan.StringId + " refused; sent to the server.");
            }
            return false;
        }

        // After the homage the house joins the realm on the server; until that reaches the PC, the conversation goes on.
        private static void ConcludePostfix(object __instance, ref bool __result)
        {
            if (__result || !Coop.OnPlayerPc || !(Get(B.Conversation.GetValue(__instance), "Clan") is Clan clan) || !Sworn.Contains(clan.StringId))
                return;
            __result = B.Admitted.GetValue(__instance) is true && B.OathSworn.GetValue(__instance) is true;
        }

        // ---- server: the answers, as the player ----

        private static void OnAnswer(Hero sender, string[] args)
        {
            if (args.Length < 2 || sender == null)
                return;
            var behaviour = RemoteCalls.Behaviour(BehaviourType);
            var outcome = "not carried out";
            if (behaviour == null)
                outcome = "Bellum's asylum is not running on the server";
            else
                Prompts.RunAsking(sender, "Bellum's asylum audience", () =>
                {
                    outcome = Answer(behaviour, sender, args);
                    // Bellum's asylum tick settles an answered audience (the guest's leave, the relation); it runs now, as
                    // it would at once in single player, and not only when the server's map next moves.
                    if (outcome.StartsWith("done")) B.Process?.Invoke(behaviour, null);
                });
            if (outcome.StartsWith("done")) _done++;
            SubModule.Log("Asylum: " + sender.StringId + "'s " + args[1] + " for " + args[0] + ": " + outcome + ".");
            if (!outcome.StartsWith("done"))
                Agency.ToPlayer(sender, "notice", "The exiles' petition could not be answered: " + outcome + ".");
        }

        private static string Answer(object behaviour, Hero ruler, string[] args)
        {
            var clan = Clan.FindFirst(c => c.StringId == args[0]);
            if (clan == null)
                return "no such house";
            if (args[1] == "estate")
                return Estate(ruler, clan, args.Length > 2 ? Settlement.Find(args[2]) : null);
            var visit = (B.Visits.GetValue(behaviour) as IDictionary)?[clan.StringId];
            if (visit == null || !(Get(visit, "State") is int state) || state != 0)
                return "no petition of theirs awaits the player";
            if (Get(visit, "Ruler") != ruler || !(B.Authority.Invoke(null, new[] { visit }) is true))
                return "the petition is not the player's to answer";
            var settlement = Get(visit, "Settlement") as Settlement;
            var guest = Get(visit, "Guest") as Hero;
            if (ruler.CurrentSettlement != settlement || guest?.CurrentSettlement != settlement)
                return "the player and " + guest?.StringId + " are not both in " + settlement?.StringId;
            if (args[1] == "refuse")
            {
                Set(visit, "State", 2);
                B.Relation.Invoke(null, new[] { visit, (object)(-10) });
                return "done, refused";
            }
            if (args[1] != "oath")
                return "unknown answer " + args[1];
            if (clan.Kingdom != null || !(Get(visit, "Deadline") is CampaignTime deadline) || !deadline.IsFuture)
                return "the petition has lapsed";
            Set(visit, "PromisedVillage", null);
            Set(visit, "State", 1);
            var admitted = B.Admission.Invoke(behaviour, new[] { visit }) is true;
            if (!admitted && clan.Kingdom == null)
                Set(visit, "State", 0);
            return admitted ? "done, " + clan.StringId + " joins " + clan.Kingdom?.StringId : "Bellum refused the admission";
        }

        // The estate: Bellum's own grant to the new vassal, from the player's house.
        private static string Estate(Hero ruler, Clan clan, Settlement village)
        {
            var lordship = B.Lordship();
            var guest = clan.Leader;
            if (village == null || lordship == null || guest == null)
                return "no such estate, or Bellum's estates are not running";
            if (clan.Kingdom == null || clan.Kingdom != ruler.Clan?.Kingdom || clan.Kingdom.RulingClan != ruler.Clan)
                return clan.StringId + " is not of the player's realm";
            var grant = AccessTools.Method(lordship.GetType(), "GetGrant", new[] { typeof(Hero) })?.Invoke(lordship, new object[] { guest });
            if (Get(grant, "Village") == village)
                return "done, already granted";
            if (!(AccessTools.Method(lordship.GetType(), "CanGrantVassal")?.Invoke(lordship, new object[] { ruler.Clan, village, guest }) is true))
                return village.StringId + " cannot be granted to " + guest.StringId;
            var call = new object[] { ruler.Clan, village, guest, ruler, null };
            return AccessTools.Method(lordship.GetType(), "TryGrantVassal")?.Invoke(lordship, call) is true ? "done, " + village.StringId + " granted" : "Bellum refused the grant";
        }

        private static object Get(object o, string name) => o == null ? null : AccessTools.Field(o.GetType(), name)?.GetValue(o);

        private static void Set(object o, string name, object value)
        {
            if (o != null) AccessTools.Field(o.GetType(), name)?.SetValue(o, value);
        }

        // Bellum's members, looked up once.
        private static class B
        {
            private static readonly Type Behaviour = AccessTools.TypeByName(BehaviourType);
            public static readonly FieldInfo Visits = F("_asylumVisits"), Conversation = F("_asylumConversation"), OathSworn = F("_asylumOathSworn"),
                Admitted = F("_asylumAdmissionSucceeded"), Granted = F("_asylumGrantSucceeded"), Offered = F("_asylumOfferedVillage");
            public static readonly MethodInfo ConversationVisit = M("ConversationAsylumVisit"), Admission = M("CompleteAsylumAdmission"),
                Relation = M("ApplyAsylumRelation"), Authority = M("AsylumAuthorityValid"), EstateAvailable = M("AsylumEstateStillAvailable"),
                Process = M("ProcessAsylumVisits");
            public static readonly MethodInfo ValidRefuge = AccessTools.TypeByName("BellumCivile.Behaviors.RefugeSelectionHelper") is Type helper ? AccessTools.Method(helper, "IsValidRefuge") : null;
            private static readonly MethodInfo PlayerRefuge = M("IsPlayerRuledValidRefuge");

            public static IEnumerable<string> Missing() => new (string, object)[] { ("_asylumVisits", Visits), ("ConversationAsylumVisit", ConversationVisit),
                ("CompleteAsylumAdmission", Admission), ("ApplyAsylumRelation", Relation), ("RefugeSelectionHelper.IsValidRefuge", ValidRefuge) }.Where(m => m.Item2 == null).Select(m => m.Item1);

            public static bool IsRefuge(Kingdom realm) => PlayerRefuge?.Invoke(null, new object[] { realm }) is true;

            public static object Lordship() => AccessTools.TypeByName("BellumCivile.Behaviors.LordshipBehavior") is Type t ? AccessTools.Property(t, "Instance")?.GetValue(null, null) : null;

            private static FieldInfo F(string name) => Behaviour == null ? null : AccessTools.Field(Behaviour, name);

            private static MethodInfo M(string name) => Behaviour == null ? null : AccessTools.Method(Behaviour, name);
        }
    }
}
