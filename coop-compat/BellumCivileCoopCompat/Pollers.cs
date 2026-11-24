using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace BellumCivileCoopCompat
{
    // Player agency, Bellum's standing questions (docs/BELLUM-COOP-COVERAGE.md, stage 3e): the questions Bellum asks "the
    // player" from its tick, whenever the map is open and nothing else is asked: court business awaiting the player (a
    // title petition, a marriage review, a protection request, a court agenda decision), a succession challenge's pledge,
    // a claim fabrication's result or setback, a lordship's command and indenture questions.
    //
    // On the server those ticks ran only for the server's placeholder player, with no map screen, so they asked nobody.
    // Now, every few seconds, each poller runs once more for each connected player, as that player and as if their map
    // were open; what it asks goes to that player (Prompts). Unanswered, each poller's fallback answers, so Bellum's own
    // "a question is open" mark is released.
    public static class Pollers
    {
        private const string B = "BellumCivile.Behaviors.";

        private sealed class Poller
        {
            public string Type, Method;
            public Prompts.Fallback Fallback;
            public string What;
            public MethodInfo Target;
            public Prompts.Site Site;
            public DateTime Next;
        }

        private static readonly List<Poller> All = new List<Poller>
        {
            new Poller { Type = B + "CourtAgendaBehavior", Method = "ProcessPlayerInquiry", Fallback = Prompts.Fallback.No, What = "court business awaiting the player" },
            new Poller { Type = B + "SuccessionChallengeBehavior", Method = "ProcessChallengeInquiry", Fallback = Prompts.Fallback.No, What = "a succession challenge's pledge" },
            new Poller { Type = B + "FeudalClaimFabricationBehavior", Method = "OnTick", Fallback = Prompts.Fallback.Yes, What = "a claim fabrication's news" },
            new Poller { Type = B + "LordshipBehavior", Method = "OnTick", Fallback = Prompts.Fallback.Yes, What = "a lordship's question" },
        };

        public static double EverySeconds = 2;

        private static readonly MethodInfo ActiveStateGetter = AccessTools.PropertyGetter(typeof(GameStateManager), nameof(GameStateManager.ActiveState));
        private static readonly Dictionary<MethodBase, Poller> ByMethod = new Dictionary<MethodBase, Poller>();
        private static int _runs;

        public static string Summary() => _runs > 0 ? "; Bellum's standing questions polled for players " + _runs : "";

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            foreach (var poller in All)
            {
                var type = AccessTools.TypeByName(poller.Type);
                var method = type == null ? null : AccessTools.DeclaredMethod(type, poller.Method);
                if (method == null || method.IsStatic || method.GetParameters().Any(p => p.ParameterType != typeof(float)))
                {
                    missing.Add(poller.Type.Substring(B.Length) + "." + poller.Method);
                    continue;
                }
                poller.Target = method;
                poller.Site = new Prompts.Site("", "", null, poller.Fallback, poller.What);
                ByMethod[method] = poller;
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(Pollers), nameof(Postfix)), transpiler: new HarmonyMethod(typeof(Pollers), nameof(Transpiler)));
            }
            InstallFabricationNews(harmony, missing);
            SubModule.Log("Standing questions: Bellum's questions from its tick are put to each connected player (" + ByMethod.Count + " of " + All.Count + ").");
            if (missing.Count > 0)
                SubModule.Log("Standing questions: not found in this Bellum version: " + string.Join(", ", missing));
        }

        // The map-screen test reads a stand-in map on the server while it acts as a player, and the real screen otherwise.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == ActiveStateGetter)
                {
                    var replacement = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Pollers), nameof(ActiveState)));
                    replacement.labels.AddRange(code.labels);
                    replacement.blocks.AddRange(code.blocks);
                    yield return replacement;
                    continue;
                }
                yield return code;
            }
        }

        // ---- a claim fabrication's news ----
        //
        // Bellum queues a fabrication's progress, success or failure for "the player" in one list with no owner, and shows
        // it later. On the server, news for a player's fabrication goes to that player as a notice when Bellum writes it.

        private static FieldInfo _outcomes;
        [ThreadStatic] private static Hero _newsFor;

        private static void InstallFabricationNews(Harmony harmony, List<string> missing)
        {
            var behavior = AccessTools.TypeByName(B + "FeudalClaimFabricationBehavior");
            var belongs = behavior == null ? null : AccessTools.DeclaredMethod(behavior, "BelongsToPlayerClan");
            var outcome = behavior == null ? null : AccessTools.Inner(behavior, "PendingFabricationOutcome");
            var ctor = outcome == null ? null : AccessTools.Constructor(outcome, new[] { typeof(string), typeof(string) });
            _outcomes = behavior == null ? null : AccessTools.Field(behavior, "_pendingPlayerOutcomes");
            if (belongs == null || ctor == null || _outcomes == null || belongs.GetParameters().Length != 2)
            {
                missing.Add("a claim fabrication's news (BelongsToPlayerClan, PendingFabricationOutcome)");
                _outcomes = null;
                return;
            }
            harmony.Patch(belongs, prefix: new HarmonyMethod(typeof(Pollers), nameof(BelongsPrefix)));
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(Pollers), nameof(OutcomePostfix)));
        }

        private static bool BelongsPrefix(object[] __args, ref bool __result)
        {
            _newsFor = null;
            if (!Coop.OnServer || AsPlayer.Current != null)
                return true;
            var clan = __args[1] as Clan;
            var record = __args[0];
            var fabricator = record == null ? null : AccessTools.Property(record.GetType(), "FabricatorClanId")?.GetValue(record, null) as string;
            var owner = clan != null && Coop.IsPlayerHero(clan.Leader) ? clan
                : fabricator == null ? null : Clan.All.FirstOrDefault(c => c?.StringId == fabricator && Coop.IsPlayerHero(c.Leader));
            if (owner == null)
                return true;
            _newsFor = owner.Leader;
            __result = true;
            return false;
        }

        private static void OutcomePostfix(string title, string body)
        {
            var hero = _newsFor;
            _newsFor = null;
            if (hero == null || !Coop.OnServer)
                return;
            Agency.ToPlayer(hero, "notice", (title ?? "") + ": " + (body ?? ""));
        }

        public static GameState ActiveState(GameStateManager manager) =>
            Coop.OnServer && AsPlayer.Current != null ? InternalPeace.ActiveState(manager) : manager?.ActiveState;

        // After Bellum's own run (for the server's placeholder), every few seconds: once more for each connected player.
        private static void Postfix(object __instance, MethodBase __originalMethod)
        {
            if (!Coop.OnServer || AsPlayer.Current != null || !ByMethod.TryGetValue(__originalMethod, out var poller) || DateTime.UtcNow < poller.Next)
                return;
            poller.Next = DateTime.UtcNow.AddSeconds(EverySeconds);
            if (_outcomes != null && poller.Target.DeclaringType == _outcomes.DeclaringType)
                (_outcomes.GetValue(__instance) as System.Collections.IList)?.Clear();   // already sent to their players as notices
            var players = Hero.AllAliveHeroes.Where(h => h.Clan?.Leader == h && Prompts.CanAsk(h)).ToList();
            foreach (var hero in players)
            {
                // A float argument is the tick's time step: a long one, so a poller that waits between checks checks now.
                var args = poller.Target.GetParameters().Select(p => (object)1000f).ToArray();
                _runs++;
                Prompts.InFrame(hero, poller.Site, () => poller.Target.Invoke(__instance, args));
            }
        }
    }
}
