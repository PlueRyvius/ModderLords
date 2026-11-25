using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // A treaty hostage's fate in co-op (docs/BELLUM-COOP-COVERAGE.md, stage 4d): a player ruler who holds a hostage from a
    // hostage pact releases them early, or executes them, in conversation (HostagePactBehavior).
    //
    // The conversation runs on the player's PC, from the server's pacts (StateMirror). Bellum's answer only queues the
    // judgment in the pact (QueueHostageJudgment) and leaves it to a later tick, which on a PC is off and on the server
    // would judge as the server's own "player": the execution was cancelled there, and the release's honor went to nobody.
    // So on the PC the hostage and the choice go to the server, which queues the judgment as that player, checking what
    // Bellum checks (the player rules the receiving house, the pact holds, an execution is allowed), and resolves it at once
    // as the player: the release (both houses' hostages go home) or the execution, with Bellum's relations and honor.
    public static class Hostages
    {
        private const string BehaviourType = "BellumCivile.Behaviors.HostagePactBehavior";
        private static int _sent, _done;
        [ThreadStatic] private static bool _resolving;

        public static string Summary() => _done > 0 ? "; hostage judgments for players " + _done : _sent > 0 ? "; hostage judgments sent " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName(BehaviourType);
            var queue = type == null ? null : AccessTools.Method(type, "QueueHostageJudgment");
            var gate = type == null ? null : AccessTools.Method(type, "CanResolveHostageJudgment");
            if (queue == null || gate == null || Bellum.Protected == null || Bellum.Order == null || Bellum.Allowed == null || Bellum.Maintain == null
                || Bellum.Hostage == null || Bellum.Begin == null || Bellum.Choose == null || Bellum.EndReason == null)
            {
                SubModule.Log("Hostages: Bellum's hostage judgment not found; it stays off on players' PCs.");
                return;
            }
            harmony.Patch(queue, prefix: new HarmonyMethod(typeof(Hostages), nameof(QueuePrefix)));
            harmony.Patch(gate, prefix: new HarmonyMethod(typeof(Hostages), nameof(GatePrefix)));
            Agency.Handle("hostage", OnHostage);
            SubModule.Log("Hostages: a player's release or execution of a treaty hostage is judged on the server.");
        }

        // Player's PC: the hostage and the choice go to the server.
        private static bool QueuePrefix(bool execute)
        {
            if (!Coop.OnPlayerPc)
                return true;
            var hostage = Hero.OneToOneConversationHero;
            if (hostage != null && Agency.ToServer("hostage", hostage.StringId, execute ? "execute" : "release"))
            {
                _sent++;
                SubModule.Log("Hostages: " + (execute ? "execution" : "release") + " of " + hostage.StringId + " sent to the server.");
            }
            return false;
        }

        // Server: Bellum resolves a judgment only on its map screen with nothing open. A dedicated server has no such screen,
        // so while the server resolves a player's judgment that check passes.
        private static bool GatePrefix(ref bool __result)
        {
            if (!_resolving)
                return true;
            __result = true;
            return false;
        }

        // Server: the judgment, queued and resolved as the player.
        private static void OnHostage(Hero sender, string[] args)
        {
            if (args.Length < 2 || sender == null)
                return;
            var hostage = Hero.FindFirst(h => h.StringId == args[0]);
            var execute = args[1] == "execute";
            var behaviour = RemoteCalls.Behaviour(BehaviourType);
            var outcome = "not carried out";
            if (hostage == null || behaviour == null)
                outcome = hostage == null ? "no such hero" : "Bellum's hostage pacts are not running on the server";
            else
                Prompts.RunAsking(sender, "Bellum's hostage judgment", () => outcome = Judge(behaviour, sender, hostage, execute));
            if (outcome.StartsWith("done")) _done++;
            SubModule.Log("Hostages: " + sender.StringId + "'s " + (execute ? "execution" : "release") + " of " + args[0] + ": " + outcome + ".");
        }

        // As the player (Hero.MainHero is the sender): Bellum's QueueHostageJudgment for the hostage, then the pact resolved.
        private static string Judge(object behaviour, Hero ruler, Hero hostage, bool execute)
        {
            var pact = Bellum.Protected.Invoke(behaviour, new object[] { hostage });
            var record = pact == null ? null : Bellum.Hostage.Invoke(null, new[] { pact, hostage });
            if (pact == null || record == null)
                return "no pact holds that hostage";
            if (!(Bellum.Order.Invoke(null, new[] { pact, record, ruler }) is true))
                return "the player may not judge that hostage now";
            if (execute && !(Bellum.Allowed.Invoke(null, new[] { record }) is true))
                return "that hostage may not be executed";
            var reason = Enum.Parse(Bellum.EndReason, execute ? "Betrayal" : "EarlyRelease");
            var aggressor = execute ? (Field(record, "ReceivingHouse") as Clan)?.Kingdom : null;
            if (!(Bellum.Begin.Invoke(pact, new[] { reason, aggressor }) is true))
                return "the pact is already ending";
            Set(pact, "TerminatingHostage", hostage);
            Set(pact, "TerminatingRuler", ruler);
            Set(record, "DispositionRuler", ruler);
            if (execute)
                Bellum.Choose.Invoke(record, new[] { Enum.Parse(Bellum.Outcome, "Execute") });
            _resolving = true;
            try { Bellum.Maintain.Invoke(behaviour, new[] { pact, (object)false }); }
            finally { _resolving = false; }
            var phase = Field(pact, "Phase")?.ToString();
            if (execute)
                return hostage.IsDead ? "done, executed" : "not carried out (pact " + phase + ")";
            return !hostage.IsPrisoner ? "done, released" : "not carried out (pact " + phase + ")";
        }

        private static object Field(object o, string name) => o == null ? null : AccessTools.Field(o.GetType(), name)?.GetValue(o);

        private static void Set(object o, string name, object value) => AccessTools.Field(o.GetType(), name)?.SetValue(o, value);

        // Bellum's members, looked up once.
        private static class Bellum
        {
            private static readonly Type Behaviour = AccessTools.TypeByName(BehaviourType);
            private static readonly Type Pact = AccessTools.TypeByName("BellumCivile.HostagePactRecord");
            private static readonly Type Record = AccessTools.TypeByName("BellumCivile.TreatyHostageRecord");
            public static readonly Type EndReason = AccessTools.TypeByName("BellumCivile.HostagePactEndReason");
            public static readonly Type Outcome = AccessTools.TypeByName("BellumCivile.HostageCustodyOutcome");
            public static readonly System.Reflection.MethodInfo Protected = Behaviour == null ? null : AccessTools.Method(Behaviour, "GetProtectedPact");
            public static readonly System.Reflection.MethodInfo Order = Behaviour == null ? null : AccessTools.Method(Behaviour, "CanOrderHostageTermination");
            public static readonly System.Reflection.MethodInfo Allowed = Behaviour == null ? null : AccessTools.Method(Behaviour, "ExecutionAllowed");
            public static readonly System.Reflection.MethodInfo Maintain = Behaviour == null || Pact == null ? null
                : AccessTools.Method(Behaviour, "Maintain", new[] { Pact, typeof(bool) });
            public static readonly System.Reflection.MethodInfo Hostage = AccessTools.TypeByName("BellumCivile.HostagePactText") is Type text ? AccessTools.Method(text, "Hostage") : null;
            public static readonly System.Reflection.MethodInfo Begin = Pact == null ? null : AccessTools.Method(Pact, "TryBeginResolution");
            public static readonly System.Reflection.MethodInfo Choose = Record == null || Outcome == null ? null : AccessTools.Method(Record, "TryChoose", new[] { Outcome });
        }
    }
}
