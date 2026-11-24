using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BellumCivileCoopCompat
{
    // Players' PCs: Bellum's politics run on the server only.
    //
    // Every PC runs all of Bellum, and Coop never sends Bellum's own state, so each player's PC had
    // its own rebellions, pacts, councils, titles and feuds. Most of what a PC then did was refused
    // by Coop, but not all of it:
    // - Kingdom decisions Bellum's AI files (policies, expulsions, fief claimants, alliances, trade
    //   agreements, king selection) are sent to the server by Coop, so the server got one copy per
    //   player, influence waived, and a fief could be voted away twice. Bellum's own decision types
    //   were refused with "can't be proposed in Coop", daily.
    // - Prisoners and hostages freed by a PC's own treaties, and alliances its client-kingdom rules
    //   started, reached the server. A household ransom freed the kinsman without the gold.
    // - Mercenary bands made new clans on every PC that existed only there.
    // - Bellum's client-kingdom rules refused Coop's own replays of the server's wars and peace, so a
    //   client kingdom at war showed at peace on every player's PC.
    // - Bellum's relation memory replaced the relations Coop sends with each PC's own.
    // - A player's own policy, expulsion and fief proposals were queued by Bellum on that PC for its
    //   (now idle) deliberations; they go to the server as vanilla proposals instead.
    //
    // So on a player's PC: Bellum's ticks don't run, those leaks are shut, Coop's replays win, and
    // relations read as Coop sends them. What the player sees in Bellum's screens is the state they
    // got when they joined. The server's Bellum is untouched.
    internal static class ServerDecides
    {
        private const string B = "BellumCivile.Behaviors.";
        private const string P = "BellumCivile.Patches.";

        /// <summary>Bellum's campaign ticks that change state, by behaviour. A player's PC skips them.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Ticks = new Dictionary<string, string[]>
        {
            [B + "BellumIntegrationBehavior"] = new[] { "DispatchCouncilAppointments", "DispatchPartitions" },
            [B + "CivilWarConflictBehavior"] = new[] { "ResumePendingCrownTransfers", "ResumePendingRivalPromotions" },
            [B + "CivilWarInterventionBehavior"] = new[] { "OnDailyTick" },
            [B + "CivilWarResolutionBehavior"] = new[] { "OnDailyTick", "OnHourlyTick", "ProcessLandlessRealmRecovery" },
            [B + "ClaimFeudBehavior"] = new[] { "OnDailyTick" },
            [B + "ClaimFeudWarBehavior"] = new[] { "OnDailyTick" },
            [B + "ClientKingdomBehavior"] = new[] { "OnDailyTick" },
            [B + "CompanionSubinfeudationBehavior"] = new[] { "RetryHouseholdMoves" },
            [B + "ComradesInArmsBehavior"] = new[] { "OnDailyTickHero" },
            [B + "ConflictCallResponseBehavior"] = new[] { "OnTick" },
            [B + "ConflictOutcomeBehavior"] = new[] { "Process" },
            [B + "ControversyBehavior"] = new[] { "OnDailyTick", "OnWeeklyTick" },
            [B + "CouncilAppointmentDeliberationBehavior"] = new[] { "OnDailyTick" },
            [B + "CouncilIncidentBehavior"] = new[] { "OnDailyTick", "OnTick" },
            [B + "CourtAgendaBehavior"] = new[]
            {
                "Tick", "MaintainClaimObjectives", "MaintainDynasticObjectives", "MaintainProtectionOffers", "MaintainRallies",
                "MaintainTitleGrants", "MaintainTradeObjectives", "ProcessPlayerInquiry",
            },
            [B + "CrownAccessionBehavior"] = new[] { "ResumePending", "ProcessPlayerCrownDepartures" },
            [B + "DefensivePactAiBehavior"] = new[] { "OnDailyTick" },
            [B + "DefensivePactBehavior"] = new[]
                { "MaintainFrontierCache", "MaintainMemberships", "PruneCompletedPacts", "RecoverJoinVotes", "ProcessPendingCalls", "ProcessPlayerNotice" },
            [B + "DynamicMercenaryBandBehavior"] = new[] { "OnDailyTickClan", "OnDailyTick", "OnTick" },
            [B + "DynamicRelationBehavior"] = new[] { "OnDailyTick", "ProcessPendingRelationWork" },
            [B + "DynasticClaimBehavior"] = new[] { "OnDailyTick" },
            [B + "DynasticHeirBehavior"] = new[] { "OnDailyTick", "RefreshChangedHouseholds" },
            [B + "ElectiveContestBehavior"] = new[] { "AdvanceControlledTests" },
            [B + "ElectiveSuccessionBehavior"] = new[] { "ProcessInquiry" },
            [B + "ExiledClanRecoveryBehavior"] = new[] { "OnDailyTick", "ProcessAsylumVisits", "OnAsylumTick", "OnWeeklyTick" },
            [B + "ExpulsionDeliberationBehavior"] = new[] { "OnDailyTick" },
            [B + "FactionManagerBehavior"] = new[] { "OnDailyTick", "MaintainCivilWarGrace", "ProcessSuccessionOpportunities", "OnWeeklyTick" },
            [B + "FeudalClaimFabricationBehavior"] = new[] { "OnDailyTick", "OnTick" },
            [B + "FeudalDeJureDriftBehavior"] = new[] { "OnWeeklyTick" },
            [B + "FeudalPoliticalOptionsBehavior"] = new[] { "OnDailyTick" },
            [B + "FeudalTitleBehavior"] = new[] { "OnHourlyTick", "ProcessIndependenceRulerRepair", "OnWeeklyTick" },
            [B + "FiefDeliberationBehavior"] = new[] { "OnDailyTick", "OnHourlyTick" },
            [B + "ForeignPolicyBehavior"] = new[] { "OnDailyTick" },
            [B + "ForeignTreatyBehavior"] = new[] { "OnDailyTick", "OnHourlyTick", "OnTick" },
            [B + "HostagePactBehavior"] = new[] { "MaintainDailyCustody", "MaintainCustody", "ProcessHostageJudgment" },
            [B + "IdeologyBehavior"] = new[] { "OnDailyTick", "OnTick" },
            [B + "IdeologyEventShockBehavior"] = new[] { "OnDailyTick" },
            [B + "LordshipBehavior"] = new[] { "OnClanDay", "Maintain", "OnTick" },
            [B + "PartitionSuccessionBehavior"] = new[] { "OnDailyTick", "OnHourlyTick" },
            [B + "PolicyDeliberationBehavior"] = new[] { "OnDailyTick" },
            [B + "PrivyCouncilBehavior"] = new[] { "OnDailyTick", "OnTick", "OnWeeklyTick" },
            [B + "ProxyWarBehavior"] = new[] { "OnDailyTick", "OnTick" },
            [B + "RealmPeaceEnforcementBehavior"] = new[] { "ResumeDecrees" },
            [B + "RegencyBehavior"] = new[] { "OnDailyTick", "OnHourlyTick", "OnTick" },
            [B + "RetainedTreatyPrisonerBehavior"] = new[] { "OnDailyTick" },
            [B + "StrategicMarriageBehavior"] = new[] { "OnDailyTickClan" },
            [B + "SuccessionChallengeBehavior"] = new[] { "Maintain", "ProcessChallengeInquiry" },
            [B + "SuccessionLawBehavior"] = new[] { "OnDailyTick", "OnHourlyTick" },
            [B + "TitleAcquisitionBehavior"] = new[] { "Process" },
            [B + "VoluntaryPactAiBehavior"] = new[] { "OnDailyTick" },
            [B + "VoluntaryPactProposalBehavior"] = new[] { "AdvanceTargetChoices", "ReconcileBallots", "RecoverDeliveries", "ReportOutcomes" },
            [B + "WarPeaceRevampBehavior"] = new[] { "OnDailyTickClan", "OnDailyTick", "OnTick" },
            [B + "WarScoreBehavior"] = new[] { "OnDailyTick" },
        };

        /// <summary>Calls that reach the server from a player's PC (or make a clan only there). Refused there.</summary>
        public static readonly IReadOnlyList<(string Type, string Method)> Leaks = new[]
        {
            (B + "IdeologyBehavior", "AddDecisionAsModAction"),
            (B + "RetainedTreatyPrisonerBehavior", "ReleaseByTreaty"),
            (B + "HostagePactBehavior", "Release"),
            (B + "HouseholdRansomBehavior", "HandleHouseholdRansomSelection"),
            (B + "ClientKingdomBehavior", "EnsureProtectedAgreements"),
            ("BellumCivile.DynamicMercenaryBandService", "TryCreateBand"),
            ("BellumCivile.DynamicMercenaryBandService", "TryJoinBand"),
        };

        /// <summary>Bellum's client-kingdom rules. On a player's PC they let Coop's replays of the server's diplomacy through.</summary>
        public static readonly IReadOnlyList<(string Type, string Method)> Replays = new[]
        {
            (B + "ClientKingdomBehavior", "CanDeclareWar"),
            (B + "ClientKingdomBehavior", "CanMakePeace"),
            (B + "ClientKingdomBehavior", "CanStartAlliance"),
            (B + "ClientKingdomBehavior", "CanEndAlliance"),
            (B + "ClientKingdomBehavior", "CanMakeTradeAgreement"),
            (B + "ClientKingdomBehavior", "CanEndTradeAgreement"),
        };

        /// <summary>
        /// Bellum's patches that take a player's vanilla policy, expulsion, fief and diplomacy proposals into its own
        /// deliberations. On a player's PC they answer "go on", so Coop sends the proposal to the server.
        /// </summary>
        public static readonly IReadOnlyList<(string Type, string Method)> Proposals = new[]
        {
            (P + "BlockVanillaPolicyPatch", "Prefix"),
            (P + "BlockVanillaExpulsionPatch", "Prefix"),
            (P + "BlockVanillaFiefVotePatch", "Prefix"),
            (P + "BlockVanillaForeignPolicyDecisionPatch", "Prefix"),
            (P + "PolicyDeliberationMenuPatch", "Prefix"),
            (P + "ExpulsionDeliberationMenuPatch", "Prefix"),
            (P + "KingdomPolicyButtonPatch", "Prefix"),
        };

        public const string Relations = B + "DynamicRelationBehavior";

        private static readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();

        /// <summary>How many times a player's PC refused each call, by "Type.Method", for tests.</summary>
        public static IReadOnlyDictionary<string, int> Refused
        {
            get { lock (Skipped) return new Dictionary<string, int>(Skipped); }
        }

        public static string Summary()
        {
            lock (Skipped)
            {
                var leaks = Leaks.Sum(l => Skipped.TryGetValue(Short(l.Type) + "." + l.Method, out var n) ? n : 0);
                return leaks > 0 ? "; calls kept off the server " + leaks : "";
            }
        }

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            var ticks = 0;
            foreach (var kv in Ticks)
                foreach (var name in kv.Value)
                    ticks += Patch(harmony, kv.Key, name, nameof(Skip), voidOnly: true, missing);
            var leaks = Leaks.Sum(l => Patch(harmony, l.Type, l.Method, nameof(Refuse), voidOnly: false, missing));
            var replays = Replays.Sum(r => Patch(harmony, r.Type, r.Method, nameof(Allow), voidOnly: false, missing));
            var proposals = Proposals.Sum(p => Patch(harmony, p.Type, p.Method, nameof(Allow), voidOnly: false, missing));
            var relations = PatchRelations(harmony, missing);

            SubModule.Log("Server decides: on a player's PC Bellum's politics are the server's (" + ticks + " ticks off, " + leaks + " calls kept off the server, "
                + replays + " client-kingdom rules let Coop's diplomacy through, " + proposals + " proposals sent to the server, relations "
                + (relations ? "as Coop sends them" : "left to Bellum") + ").");
            if (missing.Count > 0)
                SubModule.Log("Server decides: not found in this Bellum version: " + string.Join(", ", missing));
        }

        private static int Patch(Harmony harmony, string typeName, string method, string prefix, bool voidOnly, List<string> missing)
        {
            var type = AccessTools.TypeByName(typeName);
            var targets = type == null
                ? new List<MethodInfo>()
                : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == method && !m.IsAbstract && (!voidOnly || m.ReturnType == typeof(void))
                        && (prefix != nameof(Allow) || m.ReturnType == typeof(bool)))
                    .ToList();
            if (targets.Count == 0)
            {
                missing.Add(Short(typeName) + "." + method);
                return 0;
            }
            var count = 0;
            foreach (var target in targets)
            {
                try
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(ServerDecides), prefix) { priority = Priority.First });
                    count++;
                }
                catch (Exception ex) { missing.Add(Short(typeName) + "." + method + " (" + ex.GetBaseException().Message + ")"); }
            }
            return count;
        }

        private static bool PatchRelations(Harmony harmony, List<string> missing)
        {
            var type = AccessTools.TypeByName(Relations);
            var read = type == null ? null : AccessTools.Method(type, "GetRelationForRead");
            var defer = type == null ? null : AccessTools.Method(type, "DeferBackgroundRelationSet");
            var capture = type == null ? null : AccessTools.Method(type, "CaptureRelationSet");
            if (read == null || read.ReturnType != typeof(int) || read.GetParameters().Length != 3 || defer == null || defer.ReturnType != typeof(bool) || capture == null)
            {
                missing.Add("DynamicRelationBehavior (relation reads)");
                return false;
            }
            harmony.Patch(read, prefix: new HarmonyMethod(typeof(ServerDecides), nameof(StoredRelation)) { priority = Priority.First });
            harmony.Patch(defer, prefix: new HarmonyMethod(typeof(ServerDecides), nameof(Refuse)) { priority = Priority.First });
            harmony.Patch(capture, prefix: new HarmonyMethod(typeof(ServerDecides), nameof(Skip)) { priority = Priority.First });
            return true;
        }

        private static string Short(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);

        private static void Count(MethodBase method)
        {
            var key = Short(method.DeclaringType?.FullName ?? "?") + "." + method.Name;
            lock (Skipped) Skipped[key] = Skipped.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private static bool Skip(MethodBase __originalMethod)
        {
            if (!Coop.OnPlayerPc)
                return true;
            Count(__originalMethod);
            return false;
        }

        // Void calls are skipped; bool ones answer false (nothing done), other results their default.
        private static bool Refuse(MethodBase __originalMethod)
        {
            if (!Coop.OnPlayerPc)
                return true;
            Count(__originalMethod);
            if (__originalMethod is MethodInfo m && m.ReturnType != typeof(void) && m.ReturnType != typeof(bool) && m.ReturnType.IsValueType)
                return true; // unknown result shape: leave Bellum alone rather than return garbage
            return false;
        }

        private static bool Allow(ref bool __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = true;
            return false;
        }

        // Bellum's read override: the stored value is what Coop sent from the server.
        private static bool StoredRelation(int __2, ref int __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = __2;
            return false;
        }
    }
}
