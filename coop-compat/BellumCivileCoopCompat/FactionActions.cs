using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Player agency, actions (docs/BELLUM-COOP-COVERAGE.md, stage 3, A2): the Factions tab's buttons are carried out by
    // the server, as the player who pressed them.
    //
    // On a player's PC, Join (or leave), Found (confirm), Ultimatum and Create (abandon, surrender, disband) don't change
    // that PC. They send what was selected (the faction by realm, leader and type; the action by kind, type, title and
    // holder). The server opens Bellum's own Factions screen model for that player (FactionsWindowVM, in the player's
    // scope, so it reads the player's clan and realm), selects the same faction and action from the server's state, and
    // presses the same button. Bellum's own checks and costs apply there. A confirmation Bellum asks comes to the player
    // as a question (Prompts); what Bellum tells the player comes as a notice. Then the player's PC gets the server's
    // state, so the screen shows the result.
    public static class FactionActions
    {
        private const string Window = "BellumCivile.UI.FactionsWindowVM";

        public static readonly string[] Buttons = { "ExecuteJoin", "ExecuteConfirmCreate", "ExecuteUltimatum", "ExecuteCreate" };

        private static int _done;

        public static string Summary() => _done > 0 ? "; faction actions carried out for players " + _done : "";

        public static void Install(Harmony harmony)
        {
            Agency.Handle("factions", OnRequest);
            var window = AccessTools.TypeByName(Window);
            SubModule.Log("Faction actions: " + (window == null ? "Bellum's Factions screen was not found; its buttons stay off in co-op." : "the Factions tab's buttons are carried out by the server."));
        }

        /// <summary>The selected faction as the server can find it again: "realm|leader clan|type", or "feud|record id".</summary>
        public static string Key(object backend)
        {
            if (backend == null) return "";
            var type = backend.GetType();
            var kingdom = AccessTools.Property(type, "ParentKingdom")?.GetValue(backend, null) as Kingdom;
            var leader = AccessTools.Property(type, "Leader")?.GetValue(backend, null) as Clan;
            if (kingdom != null || leader != null)
                return (kingdom?.StringId ?? "") + "|" + (leader?.StringId ?? "") + "|" + AccessTools.Property(type, "Type")?.GetValue(backend, null);
            var record = AccessTools.Property(type, "RecordId")?.GetValue(backend, null) as string;
            return record == null ? "" : "feud|" + record;
        }

        // ---- player's PC: a button sends what was selected ----

        /// <summary>
        /// Player's PC: the button's request to the server. True when it was sent (the button then does nothing here; for
        /// Create, which only opens the type list in its usual mode, the list still opens here).
        /// </summary>
        public static bool Send(object window, string button)
        {
            if (!Coop.OnPlayerPc || Agency.Channel == null || window == null)
                return false;
            var type = window.GetType();
            var faction = AccessTools.Property(type, "SelectedFaction")?.GetValue(window, null);
            var backend = faction == null ? null : AccessTools.Property(faction.GetType(), "BackendFactionModel")?.GetValue(faction, null);
            var action = AccessTools.Property(type, "SelectedFactionType")?.GetValue(window, null);
            string Of(string name) => action == null ? "" : AccessTools.Property(action.GetType(), name)?.GetValue(action, null)?.ToString() ?? "";
            var sent = Agency.ToServer("factions", button, Key(backend), Of("ActionKind"), Of("FactionType"), Of("TargetTitleId"), Of("TargetHolderClanId"));
            if (sent && button == "ExecuteConfirmCreate")
                AccessTools.Property(type, "IsCreatingFaction")?.SetValue(window, false, null);
            return sent;
        }

        // ---- server: press the same button as that player ----

        private static void OnRequest(Hero sender, string[] args)
        {
            if (args.Length < 6 || !Buttons.Contains(args[0]))
                return;
            var button = args[0];
            var window = AccessTools.TypeByName(Window);
            if (window == null)
                return;
            var ok = Prompts.RunAsking(sender, "the Factions tab (" + button + ")", () =>
            {
                var vm = Activator.CreateInstance(window);
                try
                {
                    if (args[1].Length > 0 && !Select(vm, args[1]))
                    {
                        Agency.ToPlayer(sender, "notice", "That faction is no longer there.");
                        return;
                    }
                    if (button == "ExecuteConfirmCreate")
                    {
                        AccessTools.Method(window, "ExecuteCreate")?.Invoke(vm, null);
                        if (!SelectAction(vm, args[2], args[3], args[4], args[5]))
                        {
                            Agency.ToPlayer(sender, "notice", "That political action is not available to you now.");
                            return;
                        }
                    }
                    AccessTools.Method(window, button)?.Invoke(vm, null);
                    _done++;
                }
                finally
                {
                    try { AccessTools.Method(window, "OnFinalize")?.Invoke(vm, null); }
                    catch { }
                }
            });
            SubModule.Log((ok ? "Carried out " : "Could not carry out ") + button + " for " + sender.StringId + (args[1].Length > 0 ? " (" + args[1] + ")" : "") + ".");
            StateMirror.SendTo(sender);
        }

        private static bool Select(object vm, string key)
        {
            var type = vm.GetType();
            var items = AccessTools.Property(type, "Factions")?.GetValue(vm, null) as IEnumerable;
            foreach (var item in items?.Cast<object>() ?? Enumerable.Empty<object>())
            {
                var backend = AccessTools.Property(item.GetType(), "BackendFactionModel")?.GetValue(item, null);
                if (Key(backend) != key) continue;
                AccessTools.Method(type, "OnFactionSelected")?.Invoke(vm, new[] { item });
                return true;
            }
            return false;
        }

        private static bool SelectAction(object vm, string kind, string factionType, string title, string holder)
        {
            var type = vm.GetType();
            var items = AccessTools.Property(type, "AvailableFactionTypes")?.GetValue(vm, null) as IEnumerable;
            foreach (var item in items?.Cast<object>() ?? Enumerable.Empty<object>())
            {
                var t = item.GetType();
                string Of(string name) => AccessTools.Property(t, name)?.GetValue(item, null)?.ToString() ?? "";
                if (Of("ActionKind") != kind || Of("FactionType") != factionType || Of("TargetTitleId") != title || Of("TargetHolderClanId") != holder)
                    continue;
                AccessTools.Method(type, "OnFactionTypeSelected")?.Invoke(vm, new[] { item });
                return true;
            }
            return false;
        }
    }
}
