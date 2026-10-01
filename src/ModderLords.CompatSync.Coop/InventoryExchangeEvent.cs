using System;
using System.Collections.Generic;
using System.Linq;
using Helpers;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Player's game: raises the vanilla "player inventory exchanged" event when the inventory screen's Done goes through,
/// which co-op otherwise never does.
/// <para>
/// Coop replaces InventoryLogic.DoneLogic with a prefix that sends the trade to the server and returns false; the server
/// applies the item and gold moves (InventoryLogicInterface.ApplyDoneLogic), but neither side raises
/// CampaignEvents.PlayerInventoryExchangeEvent. Its listeners never run in co-op: vanilla's eight delivery quests (army
/// supplies, headman's grain, draught animals, ...) and mods such as Fourberie, whose loot donation to bandits is paid
/// out from it (the items left the party and nothing was counted). Single player raises it on the player's game once the
/// transfer is done, with the transaction's bought and sold items; this does the same, from the items captured before
/// Coop's prefix resets the screen. The items-discarded event is left alone: Coop applies the discard XP on the server.
/// </para>
/// <para>
/// A stash keeps what was put in it. Coop's Done ends with Reset(true), which puts both sides back as they were when the
/// screen opened, and for a stash (InventoryScreenHelper.OpenScreenAsStash) the left side is the stash roster itself,
/// owned by the game or a mod on this player's game only, not a party the server syncs back. The items left the party on
/// the server and the stash came back empty: Fourberie's donation saw nothing and the loot was lost. The stash's
/// contents at Done are put back before the event is raised, as single player leaves them.
/// </para>
/// </summary>
public static class InventoryExchangeEvent
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.InventoryExchangeEvent");
    private static bool _installTried, _warned;
    private static long _raised, _stashesKept;

    /// <summary>
    /// Patches the inventory screen's Done once, on a player's game. Safe to call every tick. Coop patches Done only once
    /// its session starts, so whether to raise the event is decided at each Done (did something skip the original?), not
    /// here: checking for Coop's prefix at install time ran at the main menu, found none and never looked again.
    /// </summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (Operations.OperationProcessSide.IsServer) return;
        try
        {
            var done = AccessTools.Method(typeof(InventoryLogic), nameof(InventoryLogic.DoneLogic))
                ?? throw new MissingMethodException("InventoryLogic.DoneLogic");
            // Above Coop's prefix, which returns false and so stops every prefix after it.
            Harmony.Patch(done, prefix: new HarmonyMethod(typeof(InventoryExchangeEvent), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(InventoryExchangeEvent), nameof(Postfix)));
            Log.Info("inventory exchange event: raised on this player's game when the inventory screen's Done goes through and the original was skipped (Coop's Done never raises it)");
        }
        catch (Exception ex) { Log.Warn("inventory exchange event not installed: " + ex.GetBaseException().Message); }
    }

    private static readonly AccessTools.FieldRef<InventoryLogic, InventoryScreenHelper.InventoryMode> Mode =
        AccessTools.FieldRefAccess<InventoryLogic, InventoryScreenHelper.InventoryMode>("_inventoryMode");
    private static readonly AccessTools.FieldRef<InventoryLogic, ItemRoster[]> Rosters =
        AccessTools.FieldRefAccess<InventoryLogic, ItemRoster[]>("_rosters");

    private static void Prefix(InventoryLogic __instance, out Transfer? __state)
    {
        __state = null;
        try
        {
            if (__instance.IsPreviewingItem) return;
            __state = new Transfer(__instance.GetBoughtItems(), __instance.GetSoldItems(), __instance.IsTrading);
            if (Mode(__instance) == InventoryScreenHelper.InventoryMode.Stash && Rosters(__instance) is { Length: > 0 } rosters && rosters[0] != null)
            {
                __state.Stash = rosters[0];
                __state.StashAtDone = new ItemRoster(rosters[0]);
            }
        }
        catch (Exception ex) { WarnOnce(ex); }
    }

    /// <summary>The original Done raises the event itself; only a Done that something replaced (Coop's) needs it raised here.</summary>
    private static void Postfix(bool __result, bool __runOriginal, Transfer? __state)
    {
        if (!__result || __runOriginal || __state == null) return;
        try
        {
            if (__state.Stash != null && __state.StashAtDone != null)
            {
                __state.Stash.Clear();
                __state.Stash.Add(__state.StashAtDone);
                _stashesKept++;
            }
        }
        catch (Exception ex) { WarnOnce(ex); }
        try
        {
            CampaignEventDispatcher.Instance?.OnPlayerInventoryExchange(__state.Bought, __state.Sold, __state.IsTrading);
            _raised++;
        }
        catch (Exception ex) { WarnOnce(ex); }
    }

    private static void WarnOnce(Exception ex)
    {
        if (_warned) return;
        _warned = true;
        // The listeners are other mods' code: the stack says whose.
        Log.Warn("inventory exchange event: " + ex.GetBaseException().Message + " at " + Shorten(ex.GetBaseException().StackTrace));
    }

    private static string Shorten(string? trace)
    {
        var lines = (trace ?? "").Split('\n');
        return string.Join(" | ", lines.Take(4).Select(l => l.Trim()));
    }

    internal static string Summary() => $"inventory exchange events raised {_raised}, stashes kept {_stashesKept}";

    private sealed class Transfer
    {
        public readonly List<(ItemRosterElement, int)> Bought;
        public readonly List<(ItemRosterElement, int)> Sold;
        public readonly bool IsTrading;
        public ItemRoster? Stash;
        public ItemRoster? StashAtDone;

        public Transfer(List<(ItemRosterElement, int)> bought, List<(ItemRosterElement, int)> sold, bool isTrading)
        {
            Bought = bought;
            Sold = sold;
            IsTrading = isTrading;
        }
    }
}
