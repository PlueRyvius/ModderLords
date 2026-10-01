using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Inventory;
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
/// </summary>
public static class InventoryExchangeEvent
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.InventoryExchangeEvent");
    private static bool _installTried, _warned;
    private static long _raised;

    /// <summary>Patches the inventory screen's Done once, on a player's game whose Coop replaces it. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (Operations.OperationProcessSide.IsServer) return;
        try
        {
            var done = AccessTools.Method(typeof(InventoryLogic), nameof(InventoryLogic.DoneLogic));
            var coop = done == null ? null : Harmony.GetPatchInfo(done)?.Prefixes
                .FirstOrDefault(p => p.PatchMethod.DeclaringType?.Assembly.GetName().Name == "GameInterface");
            if (coop == null)
            {
                Log.Info("inventory exchange event: Coop does not replace the inventory screen's Done here; vanilla raises it");
                return;
            }
            // Above Coop's prefix, which returns false and so stops every prefix after it.
            Harmony.Patch(done, prefix: new HarmonyMethod(typeof(InventoryExchangeEvent), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(InventoryExchangeEvent), nameof(Postfix)));
            Log.Info("inventory exchange event: raised on this player's game when the inventory screen's Done goes through (Coop's Done never raises it)");
        }
        catch (Exception ex) { Log.Warn("inventory exchange event not installed: " + ex.GetBaseException().Message); }
    }

    private static void Prefix(InventoryLogic __instance, out Transfer? __state)
    {
        __state = null;
        try
        {
            if (!__instance.IsPreviewingItem)
                __state = new Transfer(__instance.GetBoughtItems(), __instance.GetSoldItems(), __instance.IsTrading);
        }
        catch (Exception ex) { WarnOnce(ex); }
    }

    private static void Postfix(bool __result, Transfer? __state)
    {
        if (!__result || __state == null) return;
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
        Log.Warn("inventory exchange event: " + ex.GetBaseException().Message);
    }

    internal static string Summary() => $"inventory exchange events raised {_raised}";

    private sealed class Transfer
    {
        public readonly List<(ItemRosterElement, int)> Bought;
        public readonly List<(ItemRosterElement, int)> Sold;
        public readonly bool IsTrading;

        public Transfer(List<(ItemRosterElement, int)> bought, List<(ItemRosterElement, int)> sold, bool isTrading)
        {
            Bought = bought;
            Sold = sold;
            IsTrading = isTrading;
        }
    }
}
