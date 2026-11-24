using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace BellumCivileCoopCompat
{
    // Server: when a ruler dies, Bellum's succession law picks the heir, and the crown changes once.
    //
    // Bellum hooks the game's KillCharacterAction.ApplyInternal: when it owns a ruler's death, the game's own "pick a
    // new ruling clan and open a king vote" is skipped, and right after the hero is made dead Bellum settles the crown
    // by its law. Coop replaces that whole method on the server with its own copy, so Bellum's hook never runs: Coop
    // gave the crown to a random clan and opened a king vote, and an hour later Bellum found "a different house has
    // taken the Crown" and gave up. Bellum's succession law was dead in co-op.
    //
    // Here, on the server: Coop's ruler-death step (KillCharacterActionPatches.HandleKingdomLeaderDeath) steps aside when
    // Bellum owns the death, and Bellum's own settlement (CrownAccessionBehavior.AfterDeath) runs right after the game's
    // MakeDead, where Bellum would have put it. A player ruler's death stays Coop's (PlayersNotAi keeps Bellum's capture
    // away from players' clans). Players' PCs and single player are untouched.
    internal static class CrownAccession
    {
        public const string CoopDeaths = "GameInterface.Services.Actions.Patches.KillCharacterActionPatches";
        public const string CoopLeaderDeath = "HandleKingdomLeaderDeath";
        public const string Accession = "BellumCivile.Behaviors.CrownAccessionBehavior";

        private static MethodInfo _ownsDeath, _afterDeath;
        private static PropertyInfo _instance;
        private static int _owned, _settled;

        public static string Summary() => _owned > 0 ? "; rulers' deaths settled by Bellum " + _owned : "";

        public static void Install(Harmony harmony)
        {
            var accession = AccessTools.TypeByName(Accession);
            _instance = accession == null ? null : AccessTools.Property(accession, "Instance");
            _ownsDeath = accession == null ? null : AccessTools.Method(accession, "OwnsDeath", new[] { typeof(Kingdom), typeof(Hero) });
            _afterDeath = accession == null ? null : AccessTools.Method(accession, "AfterDeath", new[] { typeof(Hero) });
            if (_instance == null || _ownsDeath == null || _ownsDeath.ReturnType != typeof(bool) || _afterDeath == null)
            {
                SubModule.Log("Crown accession: Bellum's accession was not found in this version; a ruler's death stays Coop's.");
                return;
            }
            var makeDead = AccessTools.Method(typeof(KillCharacterAction), "MakeDead", new[] { typeof(Hero), typeof(bool) });
            if (makeDead == null)
            {
                SubModule.Log("Crown accession: the game's KillCharacterAction.MakeDead was not found; a ruler's death stays Coop's.");
                return;
            }
            // Coop's copy of the ruler-death step is patched once Coop has loaded (Hook, from the tick); MakeDead now.
            harmony.Patch(makeDead, postfix: new HarmonyMethod(typeof(CrownAccession), nameof(AfterMakeDead)));
            _harmony = harmony;
            TryHookCoop();
        }

        private static Harmony _harmony;
        private static bool _hooked, _toldMissing;

        /// <summary>Patches Coop's ruler-death step; called at install and from the tick until Coop is loaded.</summary>
        public static void TryHookCoop()
        {
            if (_hooked || _harmony == null)
                return;
            var coop = AccessTools.TypeByName(CoopDeaths);
            var leaderDeath = coop == null ? null : AccessTools.Method(coop, CoopLeaderDeath, new[] { typeof(Hero) });
            if (leaderDeath == null)
            {
                if (coop != null && !_toldMissing)
                {
                    _toldMissing = true;
                    SubModule.Log("Crown accession: Coop's ruler-death step was not found (Coop changed?); a ruler's death may change the crown twice.");
                }
                return;
            }
            _harmony.Patch(leaderDeath, prefix: new HarmonyMethod(typeof(CrownAccession), nameof(LeaderDeathPrefix)) { priority = Priority.First });
            _hooked = true;
            SubModule.Log("Crown accession: on the server a ruler's death is settled once, by Bellum's succession law.");
        }

        private static object Behavior()
        {
            try { return _instance.GetValue(null, null); }
            catch { return null; }
        }

        // Coop's step that picks a new ruling clan and opens a king vote: skipped when Bellum owns this death.
        private static bool LeaderDeathPrefix(Hero __0)
        {
            if (!Coop.OnServer)
                return true;
            try
            {
                var realm = __0?.Clan?.Kingdom;
                var behavior = Behavior();
                if (realm == null || behavior == null || !(_ownsDeath.Invoke(behavior, new object[] { realm, __0 }) is true))
                    return true;
                _owned++;
                SubModule.Log("A ruler of " + realm.StringId + " died; Bellum's succession law settles the crown (Coop's king vote skipped).");
                return false;
            }
            catch (Exception ex)
            {
                SubModule.Log("Could not ask Bellum about a ruler's death; Coop settles it: " + ex.GetBaseException().Message);
                return true;
            }
        }

        // Right after the hero is dead, as Bellum does in single player. A no-op for anyone Bellum has no record for.
        private static void AfterMakeDead(Hero victim)
        {
            if (!Coop.OnServer || victim == null)
                return;
            try
            {
                var behavior = Behavior();
                if (behavior == null)
                    return;
                _afterDeath.Invoke(behavior, new object[] { victim });
                _settled++;
            }
            catch (Exception ex) { SubModule.Log("Bellum could not settle a death: " + ex.GetBaseException().Message); }
        }
    }
}
