using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.ScreenSystem;

namespace BellumCivileCoopCompat
{
    // Civil wars and claim feuds end on the server, and only there.
    //
    // Bellum 1.5 queues the end of a civil war, a rival claimants' war or a claim feud (a council's
    // peace vote, an exhausted rebellion, a white peace) and applies the queue from its campaign tick,
    // but only while the map screen is open with no conversation, inquiry or parley on top. A
    // dedicated server never opens a map screen, so the server never ended those wars; each player's
    // PC ended its own copy instead, and Coop refused the result there.
    //
    // Server: the screen checks of that tick see a map screen, so the queue runs. Nothing else of the
    // tick changes. Players' PCs: the tick does nothing; the end arrives from the server through Coop.
    internal static class InternalPeace
    {
        public const string Behavior = "BellumCivile.Behaviors.InternalPeaceSettlementBehavior";
        public const string Tick = "OnTick";
        /// <summary>Bellum's test for "is this pair a civil war, rivalry or feud": (Kingdom, Kingdom, out string key, out bool rivalry).</summary>
        public const string Identify = "TryIdentify";
        private const string Parley = "BellumCivile.UI.Parley.PeaceParleyInterface";

        private static readonly MethodInfo ActiveStateGetter = AccessTools.PropertyGetter(typeof(GameStateManager), nameof(GameStateManager.ActiveState));
        private static readonly MethodInfo TopScreenGetter = AccessTools.PropertyGetter(typeof(ScreenManager), nameof(ScreenManager.TopScreen));

        private static MethodInfo _identify, _parleyInstance, _parleyShown;
        private static int _replaced, _ticks, _skipped;
        private static GameState _mapStand;
        private static ScreenBase _screenStand;

        public static string Summary() => _ticks > 0 ? "; internal peace ticks on the server " + _ticks : "";

        public static void Install(Harmony harmony)
        {
            var behavior = AccessTools.TypeByName(Behavior);
            var tick = behavior == null ? null : AccessTools.Method(behavior, Tick, new[] { typeof(float) });
            _identify = behavior == null ? null : AccessTools.Method(behavior, Identify);
            if (_identify != null && _identify.GetParameters().Length != 4)
                _identify = null;
            if (tick == null)
            {
                SubModule.Log("Internal peace: Bellum's settlement queue was not found (Bellum before 1.5?); left alone.");
                return;
            }
            var parley = AccessTools.TypeByName(Parley);
            _parleyInstance = parley == null ? null : AccessTools.PropertyGetter(parley, "Instance");
            _parleyShown = parley == null ? null : AccessTools.PropertyGetter(parley, "IsShown");

            harmony.Patch(tick,
                prefix: new HarmonyMethod(typeof(InternalPeace), nameof(Prefix)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(InternalPeace), nameof(Transpiler)));
            if (_replaced < 2)
                SubModule.Log("Internal peace: Bellum's settlement tick has changed shape (" + _replaced + " of its screen checks found); on the server civil wars and feuds may not end.");
            else
                SubModule.Log("Internal peace: civil wars and feuds are ended by the server; players' PCs leave it to the server.");
        }

        /// <summary>
        /// True when Bellum counts the war between these two realms as internal (a civil war, rivals for one throne, or a
        /// claim feud), whose peace Bellum settles through its own queue instead of a treaty.
        /// </summary>
        public static bool IsInternal(TaleWorlds.CampaignSystem.Kingdom first, TaleWorlds.CampaignSystem.Kingdom second)
        {
            if (_identify == null || first == null || second == null)
                return false;
            try { return _identify.Invoke(null, new object[] { first, second, null, false }) is true; }
            catch { return false; }
        }

        private static bool Prefix()
        {
            if (Coop.OnPlayerPc)
            {
                _skipped++;
                return false;
            }
            return true;
        }

        // The tick asks Game.Current.GameStateManager.ActiveState, ScreenManager.TopScreen and the parley window; each of
        // those reads goes through a method below instead, which answers as before except on the server.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            _replaced = 0;
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called)
                {
                    if (called == ActiveStateGetter)
                    {
                        _replaced++;
                        yield return Replace(code, nameof(ActiveState));
                        continue;
                    }
                    if (called == TopScreenGetter)
                    {
                        _replaced++;
                        yield return Replace(code, nameof(TopScreen));
                        continue;
                    }
                    if (_parleyInstance != null && called == _parleyInstance)
                    {
                        yield return Replace(code, nameof(ParleyInstance));
                        continue;
                    }
                    if (_parleyShown != null && called == _parleyShown)
                    {
                        yield return Replace(code, nameof(ParleyShown));
                        continue;
                    }
                }
                yield return code;
            }
        }

        private static CodeInstruction Replace(CodeInstruction code, string helper)
        {
            var replacement = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InternalPeace), helper));
            replacement.labels.AddRange(code.labels);
            replacement.blocks.AddRange(code.blocks);
            return replacement;
        }

        internal static GameState ActiveState(GameStateManager states)
        {
            var real = states?.ActiveState;
            if (!Coop.OnServer || real is MapState)
                return real;
            _ticks++;
            return _mapStand ?? (_mapStand = (GameState)FormatterServices.GetUninitializedObject(typeof(MapState)));
        }

        private static ScreenBase TopScreen()
        {
            if (!Coop.OnServer)
                return ScreenManager.TopScreen;
            return _screenStand ?? (_screenStand = (ScreenBase)FormatterServices.GetUninitializedObject(typeof(MapScreen)));
        }

        // The server has no parley window; on the server Bellum's UI class is never touched.
        private static object ParleyInstance() => Coop.OnServer ? null : _parleyInstance?.Invoke(null, null);

        private static bool ParleyShown(object parley) => parley != null && _parleyShown?.Invoke(parley, null) is true;

        /// <summary>Stands in for the map screen on the server: Bellum's tick only asks for a screen type named MapScreen.</summary>
        private sealed class MapScreen : ScreenBase
        {
        }
    }
}
