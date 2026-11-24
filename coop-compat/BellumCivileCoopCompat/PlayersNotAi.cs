using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Server: Bellum's AI leaves real players' clans, heroes and realms alone.
    //
    // Bellum tells "the player" apart with Clan.PlayerClan and Hero.MainHero. On a dedicated server those are a
    // placeholder nobody plays, so every real player's clan and realm took Bellum's AI path: an ultimatum accepted for a
    // player ruler (the crown handed over), a treason charge that executed a player's clan leader, a player's family
    // married off, split into cadet houses or put under a regent, a player's realm dragged into pacts and court wars,
    // a player's gold spent on espionage. None of it was ever put to the player.
    //
    // Each entry below is one of Bellum's AI decisions, the argument that names who it is about, and the answer given
    // when that is a player's: the AI decision does not happen. Entries are chosen so the early answer leaves nothing
    // half-done. Single player and players' PCs are untouched. Bellum's own player-facing versions of these come back,
    // carried out by the server, with player agency (docs/BELLUM-COOP-COVERAGE.md, stage 3).
    internal static class PlayersNotAi
    {
        /// <summary>What a guarded method answers for a player's clan, hero or realm.</summary>
        public enum Answer { Skip, False, True }

        /// <summary>Whose it is: a clan with a player in it, a player hero, or a realm a player rules (or is in).</summary>
        public enum Whose { Any, Ruled }

        public sealed class Target
        {
            public Target(string type, string method, string who, Answer answer, string harm, Whose whose = Whose.Any, int parameters = -1)
            {
                Type = type; Method = method; Who = who; Answer = answer; Harm = harm; Whose = whose; Parameters = parameters;
            }

            public string Type { get; }
            public string Method { get; }
            /// <summary>"name" (a parameter), "name.Prop.Prop" (a member path from it), or "this.Prop" for the instance.</summary>
            public string Who { get; }
            public Answer Answer { get; }
            public string Harm { get; }
            public Whose Whose { get; }
            /// <summary>Only overloads with this many parameters (-1: every overload with the parameter).</summary>
            public int Parameters { get; }
        }

        private const string B = "BellumCivile.Behaviors.";

        private const string F = "BellumCivile.";
        private const string Marriage = B + "BellumMarriageStrategyHelper";

        /// <summary>Bellum's AI decisions about a clan, hero or realm. Each is checked at install.</summary>
        public static readonly List<Target> Targets = new List<Target>
        {
            // Crown given away: a court faction's ultimatum to a player's realm, which Bellum's AI accepts or refuses for them.
            new Target(F + "FactionObject", "CanTriggerUltimatum", "this.ParentKingdom", Answer.False, "an ultimatum to a player's realm", Whose.Ruled),
            new Target(B + "IdeologyBehavior", "TriggerGrandCoalition", "kingdom", Answer.False, "a grand coalition against a player's realm", Whose.Ruled),

            // Clan rallied into a court faction, rebellion, civil-war side, treason solidarity or a succession challenge.
            new Target(F + "CourtMembershipEligibility", "CanBelong", "clan", Answer.False, "a player's clan put in a court faction"),
            new Target(B + "IdeologyBehavior", "OnDailyTickClan_Rebellion", "clan", Answer.Skip, "a player's clan joining or forming a rebellion"),
            new Target(F + "CivilWarSolidarityHelper", "IsEligibleCandidate", "candidate", Answer.False, "a player's clan rallied to a rebellion"),
            new Target(F + "CivilWarSolidarityHelper", "IsEligibleProjectionCandidate", "candidate", Answer.False, "a player's clan counted in a rebellion's strength"),
            new Target(B + "IdeologyBehavior", "IsEligibleTreasonSolidaritySupporter", "supporter", Answer.False, "a player's clan rallied to an accused house"),
            new Target(B + "IdeologyBehavior", "IsValidGrandCoalitionMember", "clan", Answer.False, "a player's clan put in a grand coalition"),
            new Target(B + "SuccessionChallengeBehavior", "NobleParticipant", "clan", Answer.False, "a player's clan pledged in a succession challenge"),

            // Leader executed, fiefs confiscated: treason and expulsion outcomes against a player's clan.
            new Target(B + "IdeologyBehavior", "ShouldExpelledLordRebel", "targetClan", Answer.False, "an expelled player's clan made to rebel"),
            new Target(B + "IdeologyBehavior", "CanScheduleTreasonDecree", "target", Answer.False, "a treason decree against a player's clan"),
            new Target(B + "IdeologyBehavior", "ExecuteTreasonPurge", "targetClan", Answer.Skip, "a treason purge of a player's clan (execution, confiscation)"),
            new Target(B + "IdeologyBehavior", "TryExpelledLordRebellion", "targetClan", Answer.False, "an expelled player's clan made to rebel"),

            // Family married off.
            new Target(Marriage, "IsStrategicMarriageClanAllowed", "clan", Answer.False, "a player's family in a strategic marriage"),
            new Target(Marriage, "MarriageProspectEligible", "hero.Clan", Answer.False, "a player's relative married off"),
            new Target(Marriage, "CourtMarriageParticipant", "hero.Clan", Answer.False, "a player's relative in a court marriage"),
            new Target(B + "StrategicMarriageBehavior", "OnDailyTickClan", "clan", Answer.Skip, "a player's clan seeking marriages by AI"),

            // Clan split, ruled by a regent, crown taken by Bellum law or contested.
            new Target(B + "RegencyBehavior", "CanUseRegency", "clan", Answer.False, "a regent over a player's clan"),
            new Target(B + "PartitionSuccessionBehavior", "CanPartitionClan", "clan", Answer.False, "a player's clan partitioned"),
            new Target(B + "CrownAccessionBehavior", "Capture", "victim.Clan", Answer.Skip, "a player ruler's crown passed by Bellum law"),
            new Target(F + "HereditaryRealmSuccession", "CanConsiderClan", "clan", Answer.False, "a player's clan drawn into a crown line"),
            new Target(F + "SuccessionChallengeSchedule", "IsDue", "realmId", Answer.False, "a succession challenge against a player ruler", Whose.Ruled),
            new Target(B + "SuccessionChallengeBehavior", "Available", "hero.Clan", Answer.False, "a player's relative made a challenger"),
            new Target(B + "ElectiveContestBehavior", "Capture", "ballot.Realm", Answer.Skip, "a disputed election decided for players"),

            // Hostages, pacts, war calls.
            new Target(B + "HostagePactBehavior", "ExecutionAllowed", "record.ReceivingHouse", Answer.False, "a hostage a player holds executed"),
            new Target(B + "DefensivePactAiBehavior", "Evaluate", "member", Answer.Skip, "a player's realm put in a defensive pact", Whose.Ruled),
            new Target(B + "DefensivePactBehavior", "CanDefensiveMemberDeclare", "member", Answer.False, "a player's realm called to a pact war", Whose.Ruled),
            new Target(B + "VoluntaryPactAiBehavior", "Evaluate", "source", Answer.Skip, "a pact (and hostage) offered from a player's realm", Whose.Ruled),
            new Target(B + "VoluntaryPactProposalBehavior", "CanPropose", "target", Answer.False, "a pact proposed to a player's realm", Whose.Ruled),

            // Gold spent on espionage.
            new Target(B + "ProxyWarBehavior", "IsValidAiSubterfugeKingdom", "kingdom", Answer.False, "espionage paid from a player ruler's purse", Whose.Ruled),

            // Claims, feuds, revocations and titles on a player's behalf or against them.
            new Target(B + "FeudalPoliticalOptionsBehavior", "TryRunPoliticalOptionsForClan", "clan", Answer.False, "title and claim moves made for a player's clan"),
            new Target(B + "ClaimFeudBehavior", "TryRunAutonomousLandedAmbitionEvaluationForClan", "clan", Answer.False, "claims pressed for a player's clan"),
            new Target(B + "ClaimFeudBehavior", "IsValidFeudClan", "clan", Answer.False, "a player's clan in a claim feud"),
            new Target(F + "FeudalTitlePlayerActionService", "TryExecuteRevocation", "title.DeFactoHolderClanId", Answer.False, "a player's title revoked by AI"),

            // Clan members split off into a mercenary band.
            new Target(B + "DynamicMercenaryBandBehavior", "EvaluateClan", "clan", Answer.Skip, "a player's family split into a mercenary band"),

            // Court business run for a player's realm (wars, protection, alliances, decrees).
            new Target(B + "CourtAgendaBehavior", "TryGetCourtRuler", "realm", Answer.False, "court business run for a player's realm", Whose.Ruled),
            new Target(B + "CourtAgendaBehavior", "AcceptProtection", "p.Protector", Answer.Skip, "a player's realm made to protect (and declare war)", Whose.Ruled),

            // A landless player's clan moved into a refuge realm.
            new Target(B + "ExiledClanRecoveryBehavior", "IsRecoverableExileCandidate", "clan", Answer.False, "a player's clan moved into a refuge realm"),
        };

        private static readonly Dictionary<MethodBase, Target> ByMethod = new Dictionary<MethodBase, Target>();
        private static readonly Dictionary<string, int> Kept = new Dictionary<string, int>();

        /// <summary>How often each harm was kept from a player, by "Type.Method", for tests.</summary>
        public static IReadOnlyDictionary<string, int> KeptFromPlayers
        {
            get { lock (Kept) return new Dictionary<string, int>(Kept); }
        }

        public static string Summary()
        {
            lock (Kept)
                return Kept.Count == 0 ? "" : "; Bellum's AI kept from players " + Kept.Values.Sum();
        }

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            var count = 0;
            foreach (var target in Targets)
            {
                var type = AccessTools.TypeByName(target.Type);
                var methods = type == null ? new List<MethodInfo>()
                    : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == target.Method && !m.IsAbstract && Fits(m, target))
                        .ToList();
                if (methods.Count == 0)
                {
                    missing.Add(Short(target));
                    continue;
                }
                foreach (var method in methods)
                {
                    try
                    {
                        ByMethod[method] = target;
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayersNotAi), PrefixFor(method)) { priority = Priority.First });
                        count++;
                    }
                    catch (Exception ex) { missing.Add(Short(target) + " (" + ex.GetBaseException().Message + ")"); }
                }
            }
            SubModule.Log("Players not AI: on the server Bellum's AI leaves players' clans and realms alone (" + count + " decisions).");
            if (missing.Count > 0)
                SubModule.Log("Players not AI: not found in this Bellum version: " + string.Join(", ", missing));
        }

        // The answer must fit the method: Skip only for void, False and True only for bool; the named argument must exist.
        private static bool Fits(MethodInfo method, Target target)
        {
            if (target.Parameters >= 0 && method.GetParameters().Length != target.Parameters)
                return false;
            if (target.Answer == Answer.Skip ? method.ReturnType != typeof(void) : method.ReturnType != typeof(bool))
                return false;
            var root = target.Who.Split('.')[0];
            return root == "this" ? !method.IsStatic : method.GetParameters().Any(p => p.Name == root);
        }

        private static string PrefixFor(MethodInfo method) => method.ReturnType == typeof(void) ? nameof(SkipPrefix) : nameof(BoolPrefix);

        private static string Short(Target target) => target.Type.Substring(target.Type.LastIndexOf('.') + 1) + "." + target.Method;

        private static bool SkipPrefix(MethodBase __originalMethod, object __instance, object[] __args) =>
            !IsPlayers(__originalMethod, __instance, __args);

        private static bool BoolPrefix(MethodBase __originalMethod, object __instance, object[] __args, ref bool __result)
        {
            if (!IsPlayers(__originalMethod, __instance, __args, out var target))
                return true;
            __result = target.Answer == Answer.True;
            return false;
        }

        private static bool IsPlayers(MethodBase method, object instance, object[] args) => IsPlayers(method, instance, args, out _);

        private static bool IsPlayers(MethodBase method, object instance, object[] args, out Target target)
        {
            target = null;
            if (!Coop.OnServer || !ByMethod.TryGetValue(method, out target))
                return false;
            object who;
            try { who = Resolve(method, instance, args, target.Who); }
            catch { return false; }
            if (!IsPlayers(who, target.Whose))
                return false;
            var key = Short(target);
            bool first;
            lock (Kept)
            {
                first = !Kept.ContainsKey(key);
                Kept[key] = first ? 1 : Kept[key] + 1;
            }
            if (first)
                SubModule.Log("Kept Bellum's AI from a player: " + target.Harm + " (" + key + ", " + Describe(who) + "; said once).");
            return true;
        }

        private static object Resolve(MethodBase method, object instance, object[] args, string path)
        {
            var parts = path.Split('.');
            object value;
            if (parts[0] == "this")
                value = instance;
            else
            {
                var index = Array.FindIndex(method.GetParameters(), p => p.Name == parts[0]);
                value = index >= 0 && index < args.Length ? args[index] : null;
            }
            for (var i = 1; i < parts.Length && value != null; i++)
            {
                var type = value.GetType();
                var property = AccessTools.Property(type, parts[i]);
                value = property != null ? property.GetValue(value, null) : AccessTools.Field(type, parts[i])?.GetValue(value);
            }
            return value;
        }

        /// <summary>A player's hero, a clan with a player in it, or a realm a player rules (Ruled) or is in (Any).</summary>
        public static bool IsPlayers(object who, Whose whose)
        {
            switch (who)
            {
                case Hero hero:
                    return Coop.IsPlayerHero(hero);
                case Clan clan:
                    return Coop.IsPlayerClan(clan);
                case Kingdom kingdom:
                    return whose == Whose.Ruled ? Coop.IsPlayerClan(kingdom.RulingClan) : kingdom.Clans.Any(Coop.IsPlayerClan);
                case string id when !string.IsNullOrEmpty(id):
                    var c = Clan.All.FirstOrDefault(x => x?.StringId == id);
                    if (c != null) return Coop.IsPlayerClan(c);
                    var k = Kingdom.All.FirstOrDefault(x => x?.StringId == id);
                    if (k != null) return IsPlayers(k, whose);
                    var h = Hero.FindFirst(x => x.StringId == id);
                    return h != null && Coop.IsPlayerHero(h);
                default:
                    return false;
            }
        }

        private static string Describe(object who) => who is TaleWorlds.ObjectSystem.MBObjectBase o ? o.StringId : who?.ToString() ?? "none";
    }
}
