using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        InGameTests.Register("game.coopLog", StepSide.Any, CoopLog);
        InGameTests.Register("settlement.enter", StepSide.Client, SettlementEnter);
        InGameTests.Register("party.settlement", StepSide.Any, PartySettlement);
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
        // The hero's StringId is local ("main_hero" on every player's game), so other sides find it by name.
        ctx.Report("heroName", Hero.MainHero.Name?.ToString());
    }

    /// <summary>
    /// Writes a line through Coop's own logger and checks it reaches Coop's log file, so a run never again goes without
    /// Coop's errors unnoticed. Reflection only (this module references no Coop): Common.Logging.LogManager's logger,
    /// its sinks, and the file Coop's SizeLimitedFileSink writes to.
    /// </summary>
    private static IEnumerable<StepWait?> CoopLog(StepContext ctx)
    {
        var commons = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetType("Common.Logging.LogManager", false) != null).ToList();
        ctx.Log("Coop Common assemblies: " + string.Join("; ", commons.Select(SafeLocation)));
        ctx.Assert(commons.Count > 0, "Coop's Common.Logging.LogManager is not loaded");
        ctx.Assert(commons.Count == 1, $"{commons.Count} copies of Coop's Common are loaded; Coop sets up logging in one of them only");
        var manager = commons[0].GetType("Common.Logging.LogManager", true);
        var lazy = manager.GetField("_logger", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        ctx.Assert(lazy != null, "LogManager has no _logger field");
        var logger = lazy!.GetType().GetProperty("Value")!.GetValue(lazy, null);
        var sinks = new List<object>();
        CollectSinks(logger, sinks, 0);
        var names = string.Join(", ", sinks.Select(s => s.GetType().Name));
        ctx.Report("sinks", names);
        var fileSink = sinks.FirstOrDefault(s => s.GetType().Name == "SizeLimitedFileSink");
        ctx.Assert(fileSink != null, $"Coop's logger has no file sink (sinks: {names}): it was built before Coop set up its log, so Coop's log file stays empty");
        var path = Path.GetFullPath((string)fileSink!.GetType().GetField("filePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fileSink));
        ctx.Report("file", path);
        var probe = "ModderLords test probe " + Guid.NewGuid().ToString("N");
        logger!.GetType().GetMethod("Information", new[] { typeof(string) })!.Invoke(logger, new object[] { probe });
        yield return ctx.Until(() => FileContains(path, probe), "the probe line in " + path, TimeSpan.FromSeconds(10));
        ctx.Report("value", "ok");
    }

    /// <summary>The leaf sinks under a Serilog logger: walks fields holding sinks, loggers, or lists of them.</summary>
    private static void CollectSinks(object? node, List<object> found, int depth)
    {
        if (node == null || depth > 6) return;
        var name = node.GetType().Name;
        if (depth > 0 && IsSink(node) && !name.Contains("Aggregate") && !name.Contains("Logger")) found.Add(node);
        foreach (var field in node.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string)) continue;
            var value = field.GetValue(node);
            if (value is System.Collections.IEnumerable list)
            {
                foreach (var item in list)
                    if (item != null && IsSinkOrLogger(item)) CollectSinks(item, found, depth + 1);
            }
            else if (value != null && IsSinkOrLogger(value)) CollectSinks(value, found, depth + 1);
        }
    }

    private static bool IsSink(object value) => value.GetType().GetInterfaces().Any(i => i.FullName == "Serilog.Core.ILogEventSink");

    private static bool IsSinkOrLogger(object value) => IsSink(value) || value.GetType().FullName == "Serilog.Core.Logger";

    private static bool FileContains(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(text);
        }
        catch (IOException) { return false; }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try { return assembly.Location; }
        catch { return assembly.FullName; }
    }

    // ---- settlements -----------------------------------------------------------------------------------------

    /// <summary>
    /// Enters <c>town</c> the way arriving at it does: EncounterManager.StartSettlementEncounter for this player's party
    /// (Coop's own coop.debug.settlements.enter_random_castle does the same), then waits until the game has it there.
    /// </summary>
    private static IEnumerable<StepWait?> SettlementEnter(StepContext ctx)
    {
        var settlement = SettlementArg(ctx);
        if (MobileParty.MainParty.CurrentSettlement != settlement)
        {
            ctx.Assert(MobileParty.MainParty.CurrentSettlement == null, $"the party is in {MobileParty.MainParty.CurrentSettlement?.StringId}, not on the map");
            EncounterManager.StartSettlementEncounter(MobileParty.MainParty, settlement);
        }
        yield return ctx.Until(() => MobileParty.MainParty.CurrentSettlement == settlement && Settlement.CurrentSettlement == settlement,
            $"the party to be in {settlement.StringId}", TimeSpan.FromSeconds(60));
        ctx.Report("menu", Campaign.Current.CurrentMenuContext?.GameMenu?.StringId ?? "none");
        ctx.Report("value", settlement.StringId);
    }

    /// <summary>Where a hero's party is (a settlement id, or "none"), settled; with <c>expected</c>, it must be that.</summary>
    private static IEnumerable<StepWait?> PartySettlement(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        object? expected = ctx.HasArg("expected") ? ctx.Arg("expected") : null;
        yield return ctx.Settled(() => party.CurrentSettlement?.StringId ?? "none", $"{party.StringId}'s settlement", expected, timeout: Sync);
        ctx.Report("value", party.CurrentSettlement?.StringId ?? "none");
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
    /// Opens the town's stash the way the town menu does (InventoryScreenHelper.OpenScreenAsStash on
    /// Settlement.CurrentSettlement.Stash, so settlement.enter goes first), moves
    /// <c>count</c> of <c>item</c> from the party into it, and presses Done, which is where Coop takes over.
    /// </summary>
    private static IEnumerable<StepWait?> StashDeposit(StepContext ctx)
    {
        var stash = MenuStashArg(ctx);
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

        // Wait for both to stop changing, then say what each holds: a timeout alone would hide which side moved.
        yield return ctx.Settled(() => stash.GetItemNumber(item) * 100000 + party.GetItemNumber(item), "the stash and the party's items", timeout: Sync);
        ctx.Dump("stash", Describe(stash));
        ctx.Dump("party-roster", Describe(party));
        ctx.Assert(stash.GetItemNumber(item) == stashBefore + count && party.GetItemNumber(item) == partyBefore - count,
            $"after Done the stash holds {stash.GetItemNumber(item)} {item.StringId} (was {stashBefore}, expected {stashBefore + count}) and the party {party.GetItemNumber(item)} (was {partyBefore}, expected {partyBefore - count})");
        ctx.Report("value", stash.GetItemNumber(item));
        ctx.Report("party", party.GetItemNumber(item));
    }

    /// <summary>Opens the stash and presses Done with no change, <c>times</c> times; the stash must read <c>expected</c> after each.</summary>
    private static IEnumerable<StepWait?> StashReopen(StepContext ctx)
    {
        var stash = MenuStashArg(ctx);
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

    /// <summary>
    /// The <c>heroName</c> argument (a name, as game.whoAmI reports it), or <c>hero</c> (a StringId), or on a player's
    /// game, this player's hero. A player's hero is found by name: its StringId differs between the server and the game.
    /// </summary>
    private static Hero HeroArg(StepContext ctx)
    {
        if (ctx.HasArg("heroName"))
        {
            var name = ctx.Arg("heroName");
            var found = Hero.FindAll(h => h.Name?.ToString() == name).ToList();
            if (found.Count != 1) throw new StepFailedException(found.Count == 0 ? $"no hero named '{name}' in this world" : $"{found.Count} heroes are named '{name}'");
            return found[0];
        }
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

    private static Settlement SettlementArg(StepContext ctx)
    {
        var id = ctx.Arg("town");
        return Settlement.Find(id) ?? throw new StepFailedException($"no settlement '{id}'");
    }

    private static ItemRoster StashArg(StepContext ctx)
    {
        var settlement = SettlementArg(ctx);
        return settlement.Stash ?? throw new StepFailedException($"{settlement.StringId} has no stash");
    }

    /// <summary>The stash as the town menu reaches it (Settlement.CurrentSettlement.Stash), so the party must be in the town.</summary>
    private static ItemRoster MenuStashArg(StepContext ctx)
    {
        var settlement = SettlementArg(ctx);
        if (Settlement.CurrentSettlement != settlement)
            throw new StepFailedException($"the party is in {Settlement.CurrentSettlement?.StringId ?? "no settlement"}, not {settlement.StringId}: a player opens the stash from the town menu, so run settlement.enter first");
        return Settlement.CurrentSettlement.Stash;
    }

    private static string Describe(ItemRoster roster)
    {
        var sb = new StringBuilder();
        foreach (var element in roster.OrderBy(e => e.EquipmentElement.Item?.StringId))
            sb.AppendLine($"{element.EquipmentElement.Item?.StringId}\t{element.Amount}");
        return sb.ToString();
    }
}
