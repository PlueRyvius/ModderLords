using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;

namespace BellumCivileCoopCompat
{
    // Players' PCs: Bellum's own player actions are off in co-op, until they are sent to the server.
    //
    // Founding or joining a faction, an ultimatum, a feud petition, crown actions, the privy council,
    // titles, succession law and Bellum's dialogue lines (lobbying, bribes, indictments, lordships,
    // companion vassals, marriages, ransoms, asylum, spymasters, mercenary bands) all run on the
    // player's own PC. Coop refuses most of what they do, so the player saw a result nobody else had,
    // and some (a disbanded rebellion, a companion's new clan) changed that PC for good.
    //
    // Here those buttons say that in co-op the server decides, and Bellum's dialogue lines don't show.
    // Vanilla's own lines and buttons (kingdom votes, proposals, diplomacy, marriage, leaving a
    // kingdom) work as in any co-op game; where Bellum hid a vanilla line, it is back.
    internal static class PlayerActions
    {
        private const string UI = "BellumCivile.UI.";

        /// <summary>Bellum's buttons for actions only the server can carry out.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Buttons = new Dictionary<string, string[]>
        {
            [UI + "FactionsWindowVM"] = new[] { "ExecuteCreate", "ExecuteConfirmCreate", "ExecuteJoin", "ExecuteUltimatum" },
            [UI + "VanillaTabs.Kingdoms.Factions.KingdomFactionsTabVM"] = new[] { "ExecuteCreate", "ExecuteConfirmCreate", "ExecuteJoin", "ExecuteUltimatum" },
            // The rest of the Hierarchy, claim feud, succession law, election and council buttons run here: Bellum's
            // own screens and questions show on this PC, and the call that changes the campaign goes to the server
            // (RemoteCalls). The Factions tab's political action list (PoliticalActionVM) only selects.
        };

        /// <summary>Bellum's patches that hide a vanilla line it replaces with its own. On a player's PC vanilla's line is back.</summary>
        public static readonly IReadOnlyList<(string Type, string Method)> VanillaBack = new[]
        {
            ("BellumCivile.Patches.HideVanillaGenericMarriageProposalPatch", "Prefix"),
        };

        /// <summary>
        /// Bellum's behaviours whose dialogue lines show on a player's PC (docs/BELLUM-COOP-COVERAGE.md, stage 4): their
        /// conversations run on the PC from the server's state (StateMirror), and the call that changes the campaign goes to
        /// the server (RemoteCalls). The other behaviours' lines stay hidden.
        /// </summary>
        public static readonly HashSet<string> OpenDialogue = new HashSet<string>
        {
            "BellumCivile.Behaviors.LordshipBehavior",        // estates, their revocation, retainers' indentures, returning an estate
            "BellumCivile.Behaviors.ClientKingdomBehavior",   // releasing a client kingdom
            // Vote lobbying (stage 4b): persuading or bribing a lord's vote; the vote's record and the bribe go to the server.
            "BellumCivile.Behaviors.FiefDeliberationBehavior",
            "BellumCivile.Behaviors.PolicyDeliberationBehavior",
            "BellumCivile.Behaviors.ExpulsionDeliberationBehavior",
            "BellumCivile.Behaviors.CouncilAppointmentDeliberationBehavior",
            "BellumCivile.Behaviors.ElectionLobbyingBehavior",
            "BellumCivile.Behaviors.CourtAgendaBehavior",
        };

        /// <summary>
        /// Lines of those behaviours that stay hidden: what they change isn't one call the server can carry out yet (asking a
        /// lord to claim a fief himself, a treason indictment).
        /// </summary>
        public static readonly HashSet<string> HiddenLines = new HashSet<string> { "fief_query_push_npc_self", "treason_indict_start" };

        public const string Line = "In co-op, the server decides this. This Bellum Civile action isn't available to players yet.";

        private static Assembly _bellum;
        private static bool _told;
        private static int _refused, _linesHidden, _linesOpen;

        public static int LinesHidden => _linesHidden;
        public static int LinesOpen => _linesOpen;
        public static int Refused => _refused;

        public static void Install(Harmony harmony)
        {
            _bellum = AccessTools.TypeByName(BellumPeaceTargets.Treaty)?.Assembly;
            var missing = new List<string>();
            var buttons = 0;
            foreach (var kv in Buttons)
            {
                var type = AccessTools.TypeByName(kv.Key);
                foreach (var name in kv.Value)
                {
                    var method = type == null ? null : AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
                    if (method == null || method.ReturnType != typeof(void))
                    {
                        missing.Add(kv.Key.Substring(kv.Key.LastIndexOf('.') + 1) + "." + name);
                        continue;
                    }
                    var faction = kv.Key.EndsWith("FactionsWindowVM") || kv.Key.EndsWith("KingdomFactionsTabVM");
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerActions), faction && FactionActions.Buttons.Contains(name) ? nameof(FactionButton) : nameof(Refuse)) { priority = Priority.First });
                    buttons++;
                }
            }
            foreach (var (typeName, name) in VanillaBack)
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.DeclaredMethod(type, name);
                if (method == null || method.ReturnType != typeof(bool))
                {
                    missing.Add(typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + name);
                    continue;
                }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerActions), nameof(RunVanilla)) { priority = Priority.First });
            }

            var lines = 0;
            foreach (var name in new[] { "AddPlayerLine", "AddRepeatablePlayerLine", "AddDialogLine" })
            {
                var methods = typeof(CampaignGameStarter).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && m.GetParameters().Any(p => p.Name == "conditionDelegate" && p.ParameterType == typeof(ConversationSentence.OnConditionDelegate)))
                    .ToList();
                if (methods.Count == 0)
                {
                    missing.Add("CampaignGameStarter." + name);
                    continue;
                }
                foreach (var method in methods)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerActions), nameof(WrapLine)));
                    lines++;
                }
            }

            SubModule.Log("Player actions: on a player's PC Bellum's own actions say the server decides (" + buttons + " buttons, Bellum's dialogue lines hidden except "
                + string.Join(" and ", OpenDialogue.Select(t => t.Substring(t.LastIndexOf('.') + 1))) + "'s, "
                + VanillaBack.Count + " vanilla line back).");
            if (missing.Count > 0)
                SubModule.Log("Player actions: not found in this Bellum version: " + string.Join(", ", missing));
        }

        private static bool Refuse()
        {
            if (!Coop.OnPlayerPc)
                return true;
            _refused++;
            SubModule.Tell(Line);
            if (!_told)
            {
                _told = true;
                SubModule.Log("A player used one of Bellum's own actions; told them the server decides (said once).");
            }
            return false;
        }

        // The Factions tab: the server carries the button out (FactionActions). Create only opens the type list in its
        // usual mode, so the list still opens here; the other buttons do nothing on this PC.
        private static bool FactionButton(object __instance, MethodBase __originalMethod)
        {
            if (!Coop.OnPlayerPc)
                return true;
            if (FactionActions.Send(__instance, __originalMethod.Name))
                return __originalMethod.Name == "ExecuteCreate";
            return Refuse();
        }

        private static bool RunVanilla(ref bool __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = true;
            return false;
        }

        // Every dialogue line Bellum registers gets a condition that is false on a player's PC in co-op, and Bellum's own
        // condition everywhere else. Asked once per line at registration, so the caller is read from the stack.
        private static void WrapLine(string id, ref ConversationSentence.OnConditionDelegate conditionDelegate)
        {
            if (_bellum == null || !FromBellum(out var from))
                return;
            for (var type = HiddenLines.Contains(id) ? null : from; type != null; type = type.DeclaringType)
            {
                if (!OpenDialogue.Contains(type.FullName)) continue;
                _linesOpen++;
                return;
            }
            var original = conditionDelegate;
            conditionDelegate = () =>
            {
                if (Coop.OnPlayerPc)
                    return false;
                return original == null || original();
            };
            _linesHidden++;
        }

        private static bool FromBellum(out Type from)
        {
            from = null;
            try
            {
                var frames = new StackTrace(2, false).GetFrames();
                if (frames == null)
                    return false;
                // Skips this module, Harmony and the game's own CampaignGameStarter (the patched method itself).
                foreach (var frame in frames.Take(6))
                {
                    var method = Harmony.GetMethodFromStackframe(frame);
                    var assembly = method?.DeclaringType?.Assembly;
                    if (assembly == null || assembly == typeof(PlayerActions).Assembly || assembly == typeof(Harmony).Assembly
                        || assembly == typeof(CampaignGameStarter).Assembly)
                        continue;
                    from = method.DeclaringType;
                    return assembly == _bellum;
                }
            }
            catch { }
            return false;
        }
    }
}
