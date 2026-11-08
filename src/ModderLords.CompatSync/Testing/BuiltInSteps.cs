using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
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
        InGameTests.Register("game.joined", StepSide.Client, Joined);
        InGameTests.Register("game.trace", StepSide.Any, Trace);
        InGameTests.Register("party.toTownGate", StepSide.Server, PartyToTownGate);
        InGameTests.Register("settlement.enter", StepSide.Client, SettlementEnter);
        InGameTests.Register("party.settlement", StepSide.Any, PartySettlement);
        InGameTests.Register("hero.addGold", StepSide.Server, AddGold);
        InGameTests.Register("hero.gold", StepSide.Any, Gold);
        InGameTests.Register("hero.adminLevel", StepSide.Server, HeroAdminLevel);
        InGameTests.Register("hero.level", StepSide.Any, HeroLevel);
        InGameTests.Register("hero.addPerk", StepSide.Server, AddPerk);
        InGameTests.Register("hero.hasPerk", StepSide.Any, HasPerk);
        InGameTests.Register("hero.maxHitPoints", StepSide.Any, MaxHitPoints);
        InGameTests.Register("rules.set", StepSide.Server, RuleSet);
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
        // Who built Coop's logger first, as CoopWatch saw it from this module's constructor.
        ctx.Log("Coop's logger at ModderLords' construction: " + CoopWatch.LoggerAtConstruction);
        ctx.Report("loggerAtConstruction", CoopWatch.LoggerAtConstruction);
        if (CoopWatch.LoggerFirstBuiltBy != null)
        {
            ctx.Dump("logger-first-built-by", CoopWatch.LoggerFirstBuiltBy);
            ctx.Log("first built by: " + FirstForeignFrame(CoopWatch.LoggerFirstBuiltBy));
        }
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
        string SinkPath(object s) => Path.GetFullPath((string)s.GetType().GetField("filePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s));
        var fileSinks = sinks.Where(s => s.GetType().Name == "SizeLimitedFileSink").ToList();
        // The run folder's copy (CoopWatch) is ours; only a sink to any other file is Coop's own log.
        var rescue = CoopWatch.RescueLog == null ? null : Path.GetFullPath(CoopWatch.RescueLog);
        ctx.Report("runFolderCopy", rescue != null && fileSinks.Any(s => SinkPath(s) == rescue) ? rescue : "none: " + (CoopWatch.RescueFailed ?? "not attached"));
        var fileSink = fileSinks.FirstOrDefault(s => SinkPath(s) != rescue);
        ctx.Assert(fileSink != null, $"Coop's logger has no file sink of its own (sinks: {names}): it was built before Coop set up its log, so Coop's log file stays empty"
            + (rescue != null ? $"; this run's copy of Coop's log is {Path.GetFileName(rescue)} in the run folder" : ""));
        var path = SinkPath(fileSink!);
        ctx.Report("file", path);
        var probe = "ModderLords test probe " + Guid.NewGuid().ToString("N");
        logger!.GetType().GetMethod("Information", new[] { typeof(string) })!.Invoke(logger, new object[] { probe });
        yield return ctx.Until(() => FileContains(path, probe), "the probe line in " + path, TimeSpan.FromSeconds(10));
        ctx.Report("value", "ok");
    }

    /// <summary>The first stack frame outside CoopWatch, Lazy and Coop's LogManager: the code that asked for the logger.</summary>
    private static string FirstForeignFrame(string stack) =>
        stack.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at ") && !l.Contains("CoopWatch") && !l.Contains("System.Lazy") && !l.Contains("System.Environment")
                                 && !l.Contains("Common.Logging.LogManager") && !l.Contains("LazyHelper"))
        ?? "(not found; see logger-first-built-by.txt)";

    /// <summary>
    /// Waits for Coop's client to announce the join finished (ClientCampaignReady: join data applied, time caught up,
    /// loading screen hidden), so steps never race the join. Without the signal (a Coop update renamed it), waits the
    /// old 15 s after the map and reports the fallback.
    /// </summary>
    private static IEnumerable<StepWait?> Joined(StepContext ctx)
    {
        if (CoopWatch.JoinUnavailable != null)
        {
            ctx.Log("Coop's join signal is unavailable (" + CoopWatch.JoinUnavailable + "); waiting 15 s instead");
            yield return ctx.Delay(TimeSpan.FromSeconds(15));
            ctx.Report("value", "fallback: " + CoopWatch.JoinUnavailable);
            yield break;
        }
        yield return ctx.Until(() => CoopWatch.JoinReadyAt != null, "Coop's ClientCampaignReady (the join finished)", TimeSpan.FromMinutes(3));
        ctx.Report("value", "signal");
        ctx.Report("readyAt", CoopWatch.JoinReadyAt!.Value.ToString("o"));
    }

    /// <summary>
    /// From here on, logs Coop's trade and item-roster messages as they pass through its broker (CoopWatch.TracedMessages)
    /// to this game's ModderLords log. After game.pause, so the AI's trading does not fill the trace first.
    /// </summary>
    private static IEnumerable<StepWait?> Trace(StepContext ctx)
    {
        CoopWatch.ArmTrace(line => ModderLords.CompatSync.Log.Info(line));
        ctx.Assert(CoopWatch.TraceMissing != null && !CoopWatch.TraceMissing.StartsWith("arming failed") && !CoopWatch.TraceMissing.StartsWith("Coop's MessageBroker"),
            "could not trace Coop's messages: " + CoopWatch.TraceMissing);
        if (CoopWatch.TraceMissing!.Length > 0) ctx.Log("not traced (not in this Coop): " + CoopWatch.TraceMissing);
        ctx.Report("value", CoopWatch.TraceMissing.Length == 0 ? "all" : "some missing");
        yield break;
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
        // Coop's server refuses an entry from further than the game's encounter distance (party.toTownGate first).
        var distance = MobileParty.MainParty.Position.Distance(settlement.GatePosition);
        ctx.Log($"distance to {settlement.StringId}'s gate: {distance:F2}");
        ctx.Report("gateDistance", distance.ToString("F2"));
        if (MobileParty.MainParty.CurrentSettlement != settlement)
        {
            ctx.Assert(MobileParty.MainParty.CurrentSettlement == null, $"the party is in {MobileParty.MainParty.CurrentSettlement?.StringId}, not on the map");
            EncounterManager.StartSettlementEncounter(MobileParty.MainParty, settlement);
        }
        // Shorter than the step's own 60 s, so a refusal reads as this wait, with the distance logged above.
        yield return ctx.Until(() => MobileParty.MainParty.CurrentSettlement == settlement && Settlement.CurrentSettlement == settlement,
            $"the party to be in {settlement.StringId} (Coop's server approves an entry only near the gate; {distance:F2} away)", TimeSpan.FromSeconds(45));
        ctx.Report("menu", Campaign.Current.CurrentMenuContext?.GameMenu?.StringId ?? "none");
        ctx.Report("value", settlement.StringId);
    }

    /// <summary>
    /// Server: puts a hero's party at a town's gate with Coop's own console command
    /// (coop.debug.mobile_party.move_to_settlement PARTY TOWN true), so Coop syncs the move its own way; then checks the
    /// party is there. Coop's output is logged, not parsed: the position is what counts.
    /// </summary>
    private static IEnumerable<StepWait?> PartyToTownGate(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var settlement = SettlementArg(ctx);
        RunConsole(ctx, "coop.debug.mobile_party.move_to_settlement", party.StringId, settlement.StringId, "true");
        yield return ctx.Settled(() => party.Position.Distance(settlement.GatePosition) < 1f, $"{party.StringId} at {settlement.StringId}'s gate", true, timeout: Sync);
        ctx.Report("value", party.Position.Distance(settlement.GatePosition).ToString("F2"));
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

    /// <summary>
    /// Server: sets a hero's level through ModderLords' own admin command (modderlords.edit REQ hero:ID level=N), the
    /// path the App's character editor takes, so a suite checks that edit reaches the players.
    /// </summary>
    private static IEnumerable<StepWait?> HeroAdminLevel(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        var level = ctx.ArgInt("level");
        ctx.Assert(hero.Level != level, $"{hero.StringId} is already level {level}; pick another so the edit changes something");
        var reply = RunConsole(ctx, "modderlords.edit", "test", "hero:" + hero.StringId, "level=" + level);
        ctx.Assert(reply.Contains("\"ok\":true"), "modderlords.edit refused: " + reply);
        yield return ctx.Settled(() => hero.Level, $"{hero.StringId}'s level", level, timeout: Sync);
        ctx.Report("value", hero.Level);
    }

    /// <summary>A hero's level, settled; with <c>expected</c>, it must be that.</summary>
    private static IEnumerable<StepWait?> HeroLevel(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.ArgInt("expected") : (object?)null;
        yield return ctx.Settled(() => hero.Level, $"{hero.StringId}'s level", expected, timeout: Sync);
        ctx.Report("value", hero.Level);
    }

    /// <summary>
    /// Server: opens a perk for a hero with HeroDeveloper.AddPerk, the call Coop's character-screen handler makes on the
    /// server when a player confirms a perk. Reports the hero's max hit points after it.
    /// </summary>
    private static IEnumerable<StepWait?> AddPerk(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        var perk = PerkArg(ctx);
        ctx.Assert(!hero.GetPerkValue(perk), $"{hero.StringId} already has {perk.StringId}; pick another so the step changes something");
        hero.HeroDeveloper.AddPerk(perk);
        yield return ctx.Settled(() => hero.GetPerkValue(perk) ? "true" : "false", $"{hero.StringId} to have {perk.StringId}", "true", timeout: Sync);
        ctx.Report("maxHitPoints", hero.MaxHitPoints);
        ctx.Report("value", "true");
    }

    /// <summary>Whether a hero has a perk ("true"/"false"), settled; with <c>expected</c>, it must be that.</summary>
    private static IEnumerable<StepWait?> HasPerk(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        var perk = PerkArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.Arg("expected").ToLowerInvariant() : null;
        yield return ctx.Settled(() => hero.GetPerkValue(perk) ? "true" : "false", $"whether {hero.StringId} has {perk.StringId}", expected, timeout: Sync);
        ctx.Report("value", hero.GetPerkValue(perk) ? "true" : "false");
    }

    /// <summary>A hero's maximum hit points as this game calculates them (perks included), settled; with <c>expected</c>, it must be that.</summary>
    private static IEnumerable<StepWait?> MaxHitPoints(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        object? expected = ctx.HasArg("expected") ? ctx.ArgInt("expected") : (object?)null;
        yield return ctx.Settled(() => hero.MaxHitPoints, $"{hero.StringId}'s max hit points", expected, timeout: Sync);
        ctx.Report("hitPoints", hero.HitPoints);
        ctx.Report("value", hero.MaxHitPoints);
    }

    /// <summary>
    /// Server: turns one of ModderLords' gameplay rules on or off (<c>rule</c>: a ModderLordsSettings property, e.g.
    /// BothPerks; <c>value</c>: true/false), as the App's Apply does. Reflection: this assembly has no Coop reference.
    /// </summary>
    private static IEnumerable<StepWait?> RuleSet(StepContext ctx)
    {
        var name = ctx.Arg("rule");
        var settings = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ModderLords.CompatSync.Coop.ModderLordsSettings", false))
            .FirstOrDefault(t => t != null) ?? throw new StepFailedException("ModderLordsSettings is not loaded in this game (ModderLords.CompatSync.Coop)");
        var property = settings.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        ctx.Assert(property != null && property.PropertyType == typeof(bool), $"ModderLordsSettings has no bool rule '{name}'");
        var value = string.Equals(ctx.Arg("value"), "true", StringComparison.OrdinalIgnoreCase);
        property!.SetValue(null, value);
        ctx.Report("value", value ? "true" : "false");
        yield break;
    }

    /// <summary>Runs a console command in this game (Coop's and ModderLords' commands register there) and logs its output.</summary>
    private static string RunConsole(StepContext ctx, string command, params string[] args)
    {
        ctx.Assert(TaleWorlds.Library.CommandLineFunctionality.HasFunctionForCommand(command), $"the console command {command} is not registered in this game");
        var output = TaleWorlds.Library.CommandLineFunctionality.CallFunction(command, args.ToList(), out var found);
        ctx.Log($"{command} {string.Join(" ", args)} -> {output}");
        ctx.Assert(found, $"the console command {command} was not found when called");
        return output ?? "";
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
        // Coop's id for this stash: without one on a player's game, a deposit reaches the server with no source roster
        // and only the party is written (run 6 lost the grain that way).
        ctx.Report("coopId", CoopWatch.CoopId(stash));
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
        ctx.Assert(count > 0, $"count must be at least 1, not {count}");
        // One stack is moved, as one drag in the screen does: an item can sit in several stacks (one per modifier), and
        // the party's total across them is not what a single transfer can move.
        var stack = party.Where(e => e.EquipmentElement.Item == item).OrderByDescending(e => e.Amount).FirstOrDefault();
        ctx.Assert(stack.Amount >= count, $"the party's largest stack of {item.StringId} holds {stack.Amount} (of {partyBefore} in all), fewer than the {count} to deposit");
        var stashId = CoopWatch.CoopId(stash);
        ctx.Log($"Coop's id for the stash: {stashId}; for the party's roster: {CoopWatch.CoopId(party)}");
        ctx.Report("stashCoopId", stashId);

        foreach (var wait in OpenStash(ctx, stash)) yield return wait;
        var logic = InventoryScreenHelper.GetActiveInventoryState().InventoryLogic;
        logic.AddTransferCommand(TransferCommand.Transfer(count, InventoryLogic.InventorySide.PlayerInventory, InventoryLogic.InventorySide.OtherInventory,
            stack, EquipmentIndex.None, EquipmentIndex.None, Hero.MainHero.CharacterObject));
        // What Coop's Done will send: the screen's rosters after the move, before Coop takes over.
        ctx.Log($"after the move, before Done: stash {stash.GetItemNumber(item)}, party {party.GetItemNumber(item)} {item.StringId}"
            + $"; the screen's left roster is the stash: {ReferenceEquals(logic.GetElementsInRoster(InventoryLogic.InventorySide.OtherInventory), stash)}");
        foreach (var wait in DoneAndClose(ctx)) yield return wait;

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
            foreach (var wait in DoneAndClose(ctx)) yield return wait;
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

    private static IEnumerable<StepWait?> DoneAndClose(StepContext ctx)
    {
        // Exactly what the screen's Done button does (SPInventoryVM.HandleDone): CloseScreen(false), which runs the
        // logic's DoneLogic once and pops the screen. Calling DoneLogic first as well pressed Done twice: Coop sent a
        // second, stale trade (stash empty, party as it started) that took the deposit back out of the stash (run 9).
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
            // Hero.FindAll searches Campaign.Characters, which on a player's game lacks Coop's local copy of that
            // player's own hero (run 5), so this player's hero is matched by name too. On the server, a player hero
            // can be missing from it as well (both-perks run 1), so the campaign's own hero lists are searched too.
            var found = Hero.FindAll(h => h.Name?.ToString() == name)
                .Concat(Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes).Where(h => h.Name?.ToString() == name))
                .Distinct().ToList();
            if (Hero.MainHero != null && Hero.MainHero.Name?.ToString() == name && !found.Contains(Hero.MainHero)) found.Add(Hero.MainHero);
            if (found.Count != 1) throw new StepFailedException(found.Count == 0 ? $"no hero named '{name}' in this world" : $"{found.Count} heroes are named '{name}'");
            return found[0];
        }
        if (!ctx.HasArg("hero"))
            return !ctx.IsServer && Hero.MainHero != null ? Hero.MainHero : throw new StepFailedException("the plan gives no argument 'hero'");
        var id = ctx.Arg("hero");
        return Hero.FindFirst(h => h.StringId == id) ?? throw new StepFailedException($"no hero '{id}' in this world");
    }

    private static PerkObject PerkArg(StepContext ctx)
    {
        var id = ctx.Arg("perk");
        return MBObjectManager.Instance.GetObject<PerkObject>(id) ?? throw new StepFailedException($"no perk '{id}'");
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
