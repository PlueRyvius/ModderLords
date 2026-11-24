using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.Core;

namespace BellumCivileCoopCompat
{
    // Server: two more of Bellum's processors wait for the map screen, which a dedicated server never opens.
    // - CivilWarResolutionBehavior.ProcessLandlessRealmRecovery retires realms that lost all their land.
    // - ElectiveContestBehavior.AdvanceControlledTests carries a disputed election to its war or crown transfer.
    // On the server their "is the map open?" check sees a map, as InternalPeace does for the settlement queue. Players'
    // PCs skip both (ServerDecides). Nothing else in them changes.
    internal static class ServerScreens
    {
        public static readonly IReadOnlyList<(string Type, string Method)> Targets = new[]
        {
            ("BellumCivile.Behaviors.CivilWarResolutionBehavior", "ProcessLandlessRealmRecovery"),
            ("BellumCivile.Behaviors.ElectiveContestBehavior", "AdvanceControlledTests"),
        };

        private static readonly MethodInfo ActiveStateGetter = AccessTools.PropertyGetter(typeof(GameStateManager), nameof(GameStateManager.ActiveState));
        private static int _replaced;

        public static void Install(Harmony harmony)
        {
            var done = new List<string>();
            var trouble = new List<string>();
            foreach (var (typeName, name) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.DeclaredMethod(type, name);
                var label = typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + name;
                if (method == null)
                {
                    trouble.Add(label + " not found");
                    continue;
                }
                _replaced = 0;
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ServerScreens), nameof(Transpiler)));
                if (_replaced == 0) trouble.Add(label + " has changed shape (no screen check found)");
                else done.Add(label);
            }
            if (done.Count > 0)
                SubModule.Log("Server screens: the server runs " + string.Join(" and ", done) + " without a map screen.");
            if (trouble.Count > 0)
                SubModule.Log("Server screens: " + string.Join("; ", trouble) + ".");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == ActiveStateGetter)
                {
                    _replaced++;
                    var replacement = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InternalPeace), nameof(InternalPeace.ActiveState)));
                    replacement.labels.AddRange(code.labels);
                    replacement.blocks.AddRange(code.blocks);
                    yield return replacement;
                    continue;
                }
                yield return code;
            }
        }
    }
}
