using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Election;

namespace BellumCivileCoopCompat
{
    // Player agency, votes (docs/BELLUM-COOP-COVERAGE.md, stage 3, A6): players vote on Bellum's own kingdom decisions.
    //
    // Coop sends a kingdom's decisions to players only for the decision types it knows; for any other type its server
    // logs "is not synced, so the server AI decides it instead of a player vote". Coop 0.1.8 lets a mod register a
    // type with GameInterface.Services.Kingdoms.KingdomDecisionTypeRegistry: Coop carries the proposer, realm and time,
    // and the mod writes and reads the rest. Bellum's four decision types (a defensive pact's call, a voluntary pact, a
    // mandate reform, a privy council appointment) are registered on the server and on every player's PC, with their
    // saveable fields in the module's own encoding (StateMirror). Coop checks both sides registered the same types.
    public static class DecisionSync
    {
        private const string Registry = "GameInterface.Services.Kingdoms.KingdomDecisionTypeRegistry";

        public static readonly string[] Types =
        {
            "BellumCivile.DefensivePactJoinDecision",
            "BellumCivile.VoluntaryPactDecision",
            "BellumCivile.MandateReformDecision",
            "BellumCivile.PrivyCouncilAppointmentDecision",
        };

        private static bool _done, _told;

        /// <summary>The decision types registered with Coop this run, for tests.</summary>
        public static readonly List<string> Registered = new List<string>();

        public static string Summary() => Registered.Count > 0 ? "; Bellum decision types players vote on " + Registered.Count : "";

        /// <summary>Registers Bellum's decision types once Coop's registry is loaded (called from the tick until then).</summary>
        public static void TryRegister()
        {
            if (_done)
                return;
            var registry = AccessTools.TypeByName(Registry);
            if (registry == null)
                return;
            _done = true;
            var register = registry.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Register" && m.IsGenericMethodDefinition);
            if (register == null)
            {
                SubModule.Log("Votes: Coop's decision registry has changed shape; Bellum's decisions stay with the server's AI.");
                return;
            }
            var missing = new List<string>();
            foreach (var name in Types)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null || !typeof(KingdomDecision).IsAssignableFrom(type)) { missing.Add(name.Substring(name.LastIndexOf('.') + 1)); continue; }
                try
                {
                    var write = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(type, typeof(byte[])),
                        typeof(DecisionSync).GetMethod(nameof(Write), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type));
                    var read = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(byte[]), type),
                        typeof(DecisionSync).GetMethod(nameof(Read), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type));
                    register.MakeGenericMethod(type).Invoke(null, new object[] { write, read });
                    Registered.Add(type.Name);
                }
                catch (Exception ex)
                {
                    SubModule.Log("Votes: could not register " + type.Name + ": " + ex.GetBaseException().Message);
                }
            }
            SubModule.Log("Votes: players vote on Bellum's own decisions (" + string.Join(", ", Registered) + ").");
            if (missing.Count > 0)
                SubModule.Log("Votes: not found in this Bellum version: " + string.Join(", ", missing));
        }

        private static byte[] Write<T>(T decision) where T : KingdomDecision => StateMirror.EncodeObject(decision);

        private static T Read<T>(byte[] payload) where T : KingdomDecision
        {
            try { return StateMirror.DecodeObject(payload) as T; }
            catch (Exception ex)
            {
                if (!_told) { _told = true; SubModule.Log("Votes: a " + typeof(T).Name + " from the server could not be read: " + ex.GetBaseException().Message); }
                return null;
            }
        }
    }
}
