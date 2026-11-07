using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Testing;

/// <summary>
/// The steps ModderLords itself provides. Vanilla game calls only, no Coop: what Coop does with them is what is tested.
/// Steps that change the world run on the server (a change made on a player's game is overwritten by Coop's sync).
/// </summary>
internal static class BuiltInSteps
{
    private static readonly TimeSpan Sync = TimeSpan.FromSeconds(30);

    internal static void Register()
    {
        InGameTests.Register("game.onMap", StepSide.Any, OnMap);
        InGameTests.Register("game.pause", StepSide.Server, Pause);
        InGameTests.Register("game.whoAmI", StepSide.Client, WhoAmI);
        InGameTests.Register("hero.addGold", StepSide.Server, AddGold);
        InGameTests.Register("hero.gold", StepSide.Any, Gold);
        InGameTests.Register("party.addItem", StepSide.Server, AddItem);
        InGameTests.Register("party.itemCount", StepSide.Any, PartyItemCount);
        InGameTests.Register("stash.count", StepSide.Any, StashCount);
        InGameTests.Register("stash.deposit", StepSide.Client, StashDeposit);
        InGameTests.Register("stash.reopen", StepSide.Client, StashReopen);
    }

    // ---- game ------------------------------------------------------------------------------------------------

    private static IEnumerable<StepWait?> OnMap(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null && (ctx.IsServer || GameStateManager.Current?.ActiveState is MapState),
            "the campaign map", TimeSpan.FromMinutes(5));
    }

    /// <summary>Stops campaign time, so AI trade, quests and wages do not move the numbers a test reads.</summary>
    private static IEnumerable<StepWait?> Pause(StepContext ctx)
    {
        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        yield return ctx.Settled(() => Campaign.Current.TimeControlMode, "campaign time to stop", CampaignTimeControlMode.Stop);
        ctx.Report("time", CampaignTime.Now.ToString());
    }

    private static IEnumerable<StepWait?> WhoAmI(StepContext ctx)
    {
        yield return ctx.Until(() => Hero.MainHero != null && MobileParty.MainParty != null, "this player's hero");
        ctx.Report("hero", Hero.MainHero.StringId);
        ctx.Report("party", MobileParty.MainParty.StringId);
        ctx.Report("name", Hero.MainHero.Name?.ToString());
    }

    // ---- heroes ----------------------------------------------------------------------------------------------

    private static IEnumerable<StepWait?> AddGold(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        GiveGoldAction.ApplyBetweenCharacters(null, hero, ctx.ArgInt("amount"), true);
        ctx.Report("gold", hero.Gold);
        ctx.Report("value", hero.Gold);
        yield break;
    }

    private static IEnumerable<StepWait?> Gold(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.ArgInt("expected") : (object?)null;
        yield return ctx.Settled(() => hero.Gold, $"{hero.StringId}'s gold", expected, timeout: Sync);
        ctx.Report("value", hero.Gold);
    }

    // ---- items -----------------------------------------------------------------------------------------------

    private static IEnumerable<StepWait?> AddItem(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var item = ItemArg(ctx);
        party.ItemRoster.AddToCounts(item, ctx.ArgInt("count"));
        ctx.Report("value", party.ItemRoster.GetItemNumber(item));
        yield break;
    }

    private static IEnumerable<StepWait?> PartyItemCount(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var item = ItemArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.ArgInt("expected") : (object?)null;
        yield return ctx.Settled(() => party.ItemRoster.GetItemNumber(item), $"{party.StringId}'s {item.StringId}", expected, timeout: Sync);
        ctx.Dump("party-roster", Describe(party.ItemRoster));
        ctx.Report("value", party.ItemRoster.GetItemNumber(item));
    }

    private static IEnumerable<StepWait?> StashCount(StepContext ctx)
    {
        var stash = StashArg(ctx);
        var item = ItemArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.ArgInt("expected") : (object?)null;
        yield return ctx.Settled(() => stash.GetItemNumber(item), $"the stash's {item.StringId}", expected, timeout: Sync);
        ctx.Dump("stash", Describe(stash));
        ctx.Report("value", stash.GetItemNumber(item));
    }

    /// <summary>
    /// Opens the town's stash the way the town menu does (InventoryScreenHelper.OpenScreenAsStash), moves
    /// <c>count</c> of <c>item</c> from the party into it, and presses Done, which is where Coop takes over.
    /// </summary>
    private static IEnumerable<StepWait?> StashDeposit(StepContext ctx)
    {
        var stash = StashArg(ctx);
        var item = ItemArg(ctx);
        var count = ctx.ArgInt("count");
        var party = MobileParty.MainParty.ItemRoster;
        var stashBefore = stash.GetItemNumber(item);
        var partyBefore = party.GetItemNumber(item);
        ctx.Assert(partyBefore >= count, $"the party has {partyBefore} {item.StringId}, fewer than the {count} to deposit");

        foreach (var wait in OpenStash(ctx, stash)) yield return wait;
        var logic = InventoryScreenHelper.GetActiveInventoryState().InventoryLogic;
        var element = party.GetElementCopyAtIndex(party.FindIndexOfItem(item));
        logic.AddTransferCommand(TransferCommand.Transfer(count, InventoryLogic.InventorySide.PlayerInventory, InventoryLogic.InventorySide.OtherInventory,
            element, EquipmentIndex.None, EquipmentIndex.None, Hero.MainHero.CharacterObject));
        foreach (var wait in DoneAndClose(ctx, logic)) yield return wait;

        yield return ctx.Settled(() => stash.GetItemNumber(item), "the stash", stashBefore + count, timeout: Sync);
        yield return ctx.Settled(() => party.GetItemNumber(item), "the party's items", partyBefore - count, timeout: Sync);
        ctx.Dump("stash", Describe(stash));
        ctx.Report("value", stash.GetItemNumber(item));
        ctx.Report("party", party.GetItemNumber(item));
    }

    /// <summary>Opens the stash and presses Done with no change, <c>times</c> times; the stash must read <c>expected</c> after each.</summary>
    private static IEnumerable<StepWait?> StashReopen(StepContext ctx)
    {
        var stash = StashArg(ctx);
        var item = ItemArg(ctx);
        var times = ctx.ArgInt("times", 3);
        var expected = ctx.ArgInt("expected");
        var partyBefore = MobileParty.MainParty.ItemRoster.GetItemNumber(item);
        var seen = new List<int>();
        for (int i = 1; i <= times; i++)
        {
            foreach (var wait in OpenStash(ctx, stash)) yield return wait;
            foreach (var wait in DoneAndClose(ctx, InventoryScreenHelper.GetActiveInventoryState().InventoryLogic)) yield return wait;
            yield return ctx.Settled(() => stash.GetItemNumber(item), $"the stash after reopening {i}", timeout: Sync);
            seen.Add(stash.GetItemNumber(item));
            ctx.Log($"reopen {i}: stash {seen[seen.Count - 1]}, party {MobileParty.MainParty.ItemRoster.GetItemNumber(item)}");
            if (seen[seen.Count - 1] != expected) ctx.Dump($"stash-after-reopen-{i}", Describe(stash));
            ctx.Assert(seen[seen.Count - 1] == expected, $"after reopening {i} time(s) the stash holds {seen[seen.Count - 1]} {item.StringId}, expected {expected}");
        }
        var partyAfter = MobileParty.MainParty.ItemRoster.GetItemNumber(item);
        ctx.Assert(partyAfter == partyBefore, $"reopening changed the party's {item.StringId} from {partyBefore} to {partyAfter}");
        ctx.Report("value", stash.GetItemNumber(item));
    }

    private static IEnumerable<StepWait?> OpenStash(StepContext ctx, ItemRoster stash)
    {
        InventoryScreenHelper.OpenScreenAsStash(stash);
        yield return ctx.Until(() => InventoryScreenHelper.GetActiveInventoryState()?.InventoryLogic != null, "the stash screen to open", TimeSpan.FromSeconds(20));
    }

    private static IEnumerable<StepWait?> DoneAndClose(StepContext ctx, InventoryLogic logic)
    {
        // What the screen's Done button does: the logic's Done, then the screen closes.
        logic.DoneLogic();
        InventoryScreenHelper.CloseScreen(false);
        yield return ctx.Until(() => !(GameStateManager.Current?.ActiveState is InventoryState), "the stash screen to close", TimeSpan.FromSeconds(20));
    }

    // ---- arguments -------------------------------------------------------------------------------------------

    /// <summary>The <c>hero</c> argument (a StringId), or on a player's game, this player's hero.</summary>
    private static Hero HeroArg(StepContext ctx)
    {
        if (!ctx.HasArg("hero"))
            return !ctx.IsServer && Hero.MainHero != null ? Hero.MainHero : throw new StepFailedException("the plan gives no argument 'hero'");
        var id = ctx.Arg("hero");
        return Hero.FindFirst(h => h.StringId == id) ?? throw new StepFailedException($"no hero '{id}' in this world");
    }

    private static ItemObject ItemArg(StepContext ctx)
    {
        var id = ctx.Arg("item");
        return MBObjectManager.Instance.GetObject<ItemObject>(id) ?? throw new StepFailedException($"no item '{id}'");
    }

    private static ItemRoster StashArg(StepContext ctx)
    {
        var id = ctx.Arg("town");
        var settlement = Settlement.Find(id) ?? throw new StepFailedException($"no settlement '{id}'");
        return settlement.Stash ?? throw new StepFailedException($"{id} has no stash");
    }

    private static string Describe(ItemRoster roster)
    {
        var sb = new StringBuilder();
        foreach (var element in roster.OrderBy(e => e.EquipmentElement.Item?.StringId))
            sb.AppendLine($"{element.EquipmentElement.Item?.StringId}\t{element.Amount}");
        return sb.ToString();
    }
}
