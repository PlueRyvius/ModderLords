using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModderLords.Testing;
using Retinues.Configuration;
using Retinues.Features.AutoJoin;
using Retinues.Features.Experience;
using Retinues.Features.Stocks;
using Retinues.Features.Unlocks;
using Retinues.Game;
using Retinues.Game.Wrappers;
using Retinues.Managers;
using Retinues.Troops;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Tests.Retinues;

/// <summary>
/// Steps for Retinues' troop editor. Each one calls what the editor's own button calls (the managers, or the editor's
/// view-model), so in Coop it takes the same path a player's click does: the player's game relays the edit, the server
/// runs it as that player, and the troops come back in the server's snapshot.
///
/// Troops are passed by StringId: the server and the player's game use the same troop ids (the server allocates them).
/// The Coop parts of Retinues (its Coop fork) are reached by reflection, so this compiles against any Retinues build.
/// </summary>
public static class Steps
{
    private static readonly TimeSpan Sync = TimeSpan.FromSeconds(30);

    public static void Register()
    {
        InGameTests.Register("Retinues.health", StepSide.Any, Health);
        InGameTests.Register("retinues.ready", StepSide.Client, Ready);
        InGameTests.Register("retinues.coopRules", StepSide.Any, CoopRules);
        InGameTests.Register("retinues.giveFief", StepSide.Server, GiveFief);
        InGameTests.Register("retinues.ensureTroops", StepSide.Client, EnsureTroops);
        InGameTests.Register("retinues.troop", StepSide.Any, Troop);
        InGameTests.Register("retinues.rename", StepSide.Client, Rename);
        InGameTests.Register("retinues.giveXp", StepSide.Server, GiveXp);
        InGameTests.Register("retinues.skillUp", StepSide.Client, SkillUp);
        InGameTests.Register("retinues.equip", StepSide.Client, Equip);
        InGameTests.Register("retinues.unequip", StepSide.Client, Unequip);
        InGameTests.Register("retinues.addUpgrade", StepSide.Client, AddUpgrade);
        InGameTests.Register("retinues.editorPages", StepSide.Client, EditorPages);
        InGameTests.Register("retinues.openEditor", StepSide.Client, OpenEditorStep);
        InGameTests.Register("retinues.closeEditor", StepSide.Client, CloseEditorStep);
        InGameTests.Register("retinues.raiseJoinCap", StepSide.Client, RaiseJoinCap);
        InGameTests.Register("retinues.stock", StepSide.Any, Stock);
        InGameTests.Register("retinues.lockedItem", StepSide.Server, LockedItem);
        InGameTests.Register("retinues.unlock", StepSide.Server, Unlock);
        InGameTests.Register("retinues.isUnlocked", StepSide.Any, IsUnlocked);
        InGameTests.Register("retinues.watchPopups", StepSide.Client, WatchPopups);
        InGameTests.Register("retinues.unlockPopup", StepSide.Client, UnlockPopup);
        InGameTests.Register("retinues.remove", StepSide.Client, Remove);
        InGameTests.Register("retinues.noErrors", StepSide.Any, NoErrors);
    }

    // ---- health and session --------------------------------------------------------------------------------

    /// <summary>
    /// Retinues' own health, compared across sides by the core suite's mods.health: its version. Also notes where its
    /// log ends, so retinues.noErrors reads only what this run wrote.
    /// </summary>
    private static IEnumerable<StepWait?> Health(StepContext ctx)
    {
        var assembly = typeof(Player).Assembly;
        ctx.State["retinues.logStart"] = File.Exists(LogFile) ? new FileInfo(LogFile).Length : 0L;
        ctx.Log($"co-op session: {Fork.InSession}, player's game: {Fork.IsClient}, log: {LogFile}");
        ctx.Report("value", assembly.GetName().Version?.ToString());
        yield break;
    }

    /// <summary>Player: in Coop, waits until the server has accepted this game (Retinues' own hello); in single player, passes.</summary>
    private static IEnumerable<StepWait?> Ready(StepContext ctx)
    {
        if (!Fork.InSession)
        {
            ctx.Log("no co-op session");
            yield break;
        }
        ctx.Assert(Fork.HasCoopSupport, "this Retinues has no Coop support (Retinues.Mods.Coop.CoopBridge is missing)");
        yield return ctx.Until(() => Fork.Ready, "the server to accept this game's Retinues", Sync);
    }

    /// <summary>
    /// Coop: the options Retinues turns off for everyone in a session (doctrines, the global editor, edits that take
    /// time), as this game reads them. Reported, so a compare also shows both sides use the server's settings.
    /// </summary>
    private static IEnumerable<StepWait?> CoopRules(StepContext ctx)
    {
        var options = new (string Name, bool Value)[]
        {
            ("EnableDoctrines", Config.EnableDoctrines),
            ("EnableGlobalEditor", Config.EnableGlobalEditor),
            ("EquippingTroopsTakesTime", Config.EquippingTroopsTakesTime),
            ("TrainingTroopsTakesTime", Config.TrainingTroopsTakesTime),
        };
        var on = options.Where(o => o.Value).Select(o => o.Name).ToList();
        ctx.Assert(on.Count == 0, "on in a co-op session, expected off: " + string.Join(", ", on));
        ctx.Report("value", string.Join(",", options.Select(o => $"{o.Name}={o.Value}")));
        yield break;
    }

    // ---- troops --------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: gives a hero's clan a town (<c>town</c>), through the game's own action. Retinues makes a clan's regular
    /// troops only once it holds a fief; before that a clan has its two retinues alone.
    /// </summary>
    private static IEnumerable<StepWait?> GiveFief(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        var town = Settlement.Find(ctx.Arg("town"));
        ctx.Assert(town != null, $"no settlement '{ctx.Arg("town")}'");
        // On a clan's first fief Retinues asks, in a pop-up on this game, whether to copy the culture's troop tree or
        // start from scratch, and makes the troops on the answer. The step answers as a player would: copy.
        InquiryData? asked = null;
        void Capture(InquiryData data, bool pause, bool priority) => asked = data;
        InformationManager.OnShowInquiry += Capture;
        try
        {
            if (town!.OwnerClan != hero.Clan) ChangeOwnerOfSettlementAction.ApplyByDefault(hero, town);
            yield return ctx.Until(() => town.OwnerClan == hero.Clan, $"{hero.Clan?.Name} to own {town.StringId}", Sync);
            yield return ctx.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            InformationManager.OnShowInquiry -= Capture;
        }
        if (asked != null)
        {
            ctx.Log($"answered '{asked.TitleText}' with '{asked.AffirmativeText}'");
            InformationManager.HideInquiry();
            asked.AffirmativeAction?.Invoke();
        }
        ctx.Report("asked", asked?.TitleText ?? "");
        ctx.Report("value", town!.StringId);
    }

    /// <summary>
    /// Player: makes sure the player's clan has its troops, as opening the editor does, and waits until they exist here
    /// (in Coop they arrive in the server's snapshot). Reports the basic retinue's id as <c>retinue</c>.
    /// </summary>
    private static IEnumerable<StepWait?> EnsureTroops(StepContext ctx)
    {
        var clan = Player.Clan;
        ctx.Assert(clan != null, "the player has no clan");
        TroopBuilder.EnsureTroopsExist(clan!);
        yield return ctx.Until(() => clan!.RetinueBasic != null && clan.RetinueElite != null, "the clan's retinues", Sync);
        ctx.Report("retinue", clan!.RetinueBasic.StringId);
        ctx.Report("eliteRetinue", clan.RetinueElite.StringId);
        ctx.Report("basicRoot", clan.RootBasic?.StringId);
        if (ctx.Arg("regulars", "false") == "true")
        {
            yield return ctx.Until(() => clan.RootBasic != null, "the clan's regular troops (does the clan hold a fief?)", Sync);
            ctx.Report("basicRoot", clan.RootBasic!.StringId);
        }
        ctx.Report("value", clan.RetinueBasic.StringId);
    }

    /// <summary>
    /// A troop as this game holds it (name, skills, battle set, upgrade targets), settled. For compares: both sides must
    /// hold the same troop. With <c>expectedName</c>, its name must be that.
    /// </summary>
    private static IEnumerable<StepWait?> Troop(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        if (ctx.HasArg("expectedName"))
            yield return ctx.Settled(() => troop.Name, $"{troop.StringId}'s name", ctx.Arg("expectedName"), timeout: Sync);
        yield return ctx.Settled(() => Describe(troop), $"{troop.StringId}");
        var text = Describe(troop);
        ctx.Dump("troop", text);
        ctx.Report("name", troop.Name);
        ctx.Report("value", text);
    }

    /// <summary>Player: renames a troop as the editor's name box does, and waits for the name to hold.</summary>
    private static IEnumerable<StepWait?> Rename(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var name = ctx.Arg("name");
        troop.Name = name;
        // The editor sends appearance edits (name, body, culture) after it refreshes the troop.
        Fork.SendAppearanceIfChanged(troop);
        yield return ctx.Settled(() => troop.Name, $"{troop.StringId}'s name", name, timeout: Sync);
        ctx.Report("value", troop.Name);
    }

    /// <summary>Server: adds training XP to a troop (as battles do), so the player can spend it on a skill.</summary>
    private static IEnumerable<StepWait?> GiveXp(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        TroopXpBehavior.Add(troop, ctx.ArgInt("amount"));
        ctx.Report("value", TroopXpBehavior.Get(troop));
        yield break;
    }

    /// <summary>
    /// Player: raises one skill by a point, as the editor's skill arrow does. Waits for the XP to arrive first (in Coop it
    /// comes with the server's snapshot), then for the point to land.
    /// </summary>
    private static IEnumerable<StepWait?> SkillUp(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var skill = MBObjectManager.Instance.GetObject<SkillObject>(ctx.Arg("skill", "OneHanded"));
        ctx.Assert(skill != null, $"no skill '{ctx.Arg("skill", "OneHanded")}'");
        yield return ctx.Until(() => SkillManager.CanIncrementSkill(troop, skill!), $"{troop.StringId} to be able to raise {skill!.StringId} (XP {TroopXpBehavior.Get(troop)})", Sync);
        var before = troop.GetSkill(skill!);
        SkillManager.ModifySkill(troop, skill!, increment: true);
        yield return ctx.Settled(() => troop.GetSkill(skill!), $"{troop.StringId}'s {skill!.StringId}", before + 1, timeout: Sync);
        ctx.Report("before", before);
        ctx.Report("value", troop.GetSkill(skill!));
    }

    /// <summary>
    /// Player: equips an item in a battle-set slot, as picking it in the equipment list does, and checks the gold it
    /// cost was taken once. <c>slot</c> is an EquipmentIndex name (Head by default); <c>item</c> is optional: without it
    /// the cheapest item this troop may equip there, other than the current one.
    /// </summary>
    private static IEnumerable<StepWait?> Equip(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var slot = SlotArg(ctx);
        var current = troop.Loadout.Get(0).Get(slot)?.Base;
        var item = ctx.HasArg("item")
            ? new WItem(ctx.Arg("item"))
            : Candidates(troop, slot).FirstOrDefault(i => i.Base != current);
        ctx.Assert(item?.Base != null, $"no item {troop.StringId} may equip in {slot}");
        var quote = EquipmentManager.QuoteEquip(troop, 0, slot, item!);
        var goldBefore = Player.Gold;
        ctx.Log($"equipping {item!.Base.StringId} in {ctx.Arg("slot", "Head")} (quote: {quote.GoldCost} gold, {quote.CopiesFromStock} from stock); gold {goldBefore}");

        var result = EquipmentManager.TryEquip(troop, 0, slot, item);
        ctx.Assert(result.Ok, $"TryEquip refused {item.Base.StringId}: {result.Reason}");
        yield return ctx.Settled(() => troop.Loadout.Get(0).Get(slot)?.Base?.StringId, $"{troop.StringId}'s {slot}", item.Base.StringId, timeout: Sync);
        yield return ctx.Settled(() => Player.Gold, "the player's gold", goldBefore - quote.GoldCost, timeout: Sync);
        ctx.Report("item", item.Base.StringId);
        ctx.Report("cost", quote.GoldCost);
        ctx.Report("value", Player.Gold);
    }

    /// <summary>Player: empties a battle-set slot, as the slot's unequip button does; no gold changes hands.</summary>
    private static IEnumerable<StepWait?> Unequip(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var slot = SlotArg(ctx);
        var goldBefore = Player.Gold;
        var result = EquipmentManager.TryUnequip(troop, 0, slot);
        ctx.Assert(result.Ok, $"TryUnequip refused: {result.Reason}");
        yield return ctx.Settled(() => troop.Loadout.Get(0).Get(slot)?.Base?.StringId ?? "", $"{troop.StringId}'s {slot}", "", timeout: Sync);
        yield return ctx.Settled(() => Player.Gold, "the player's gold", goldBefore, timeout: Sync);
        ctx.Report("value", Player.Gold);
    }

    /// <summary>
    /// Player: adds an upgrade target, as the editor's add-upgrade button does, to the first troop of <c>troop</c>'s tree
    /// that can take one (a copied culture tree is often full at its root). Reports the new troop's id as
    /// <c>value</c> and the troop it upgrades from as <c>parent</c>.
    /// </summary>
    private static IEnumerable<StepWait?> AddUpgrade(StepContext ctx)
    {
        var root = TroopArg(ctx);
        var name = ctx.Arg("name");
        var troop = Tree(root).FirstOrDefault(UpgradeManager.CanAddUpgradeToTroop);
        ctx.Assert(troop != null, $"no troop in {root.StringId}'s tree can take an upgrade: {UpgradeManager.GetAddUpgradeToTroopReason(root)}");
        ctx.Report("parent", troop!.StringId);
        UpgradeManager.AddUpgradeTarget(troop, name);
        WCharacter? added = null;
        yield return ctx.Until(() => (added = troop.UpgradeTargets.FirstOrDefault(t => t.Name == name)) != null, $"{troop.StringId} to have an upgrade named '{name}'", Sync);
        ctx.Report("value", added!.StringId);
    }

    // ---- the editor screen ---------------------------------------------------------------------------------

    /// <summary>
    /// Player: opens the troop editor on a troop, visits its equipment page and back, and closes it. The editor reads
    /// every skill and slot, so a troop the game cannot show (Coop run 1: no skill table on the player's game) fails
    /// here, through retinues.noErrors.
    /// </summary>
    private static IEnumerable<StepWait?> EditorPages(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        Editor.SwitchScreen("Equipment");
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
        Editor.SwitchScreen("Troop");
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
        foreach (var wait in CloseEditor(ctx)) yield return wait;
    }

    /// <summary>
    /// Player: removes a troop with the editor's remove button and answers its pop-up with Confirm, then waits until the
    /// troop is gone from its parent's upgrades (<c>parent</c>).
    /// </summary>
    private static IEnumerable<StepWait?> Remove(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var parent = new WCharacter(ctx.Arg("parent"));
        var id = troop.StringId;
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;

        InquiryData? asked = null;
        void Capture(InquiryData data, bool pause, bool priority) => asked = data;
        InformationManager.OnShowInquiry += Capture;
        try
        {
            Editor.Invoke("TroopScreen", "ExecuteRemoveTroop");
            yield return ctx.Until(() => asked != null, "the remove pop-up", TimeSpan.FromSeconds(10));
        }
        finally
        {
            InformationManager.OnShowInquiry -= Capture;
        }
        ctx.Log("pop-up: " + asked!.TitleText);
        InformationManager.HideInquiry();
        asked.AffirmativeAction?.Invoke();
        yield return ctx.Until(() => parent.UpgradeTargets.All(t => t.StringId != id), $"{id} to be gone from {parent.StringId}'s upgrades", Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", string.Join(",", parent.UpgradeTargets.Select(t => t.StringId)));
    }

    /// <summary>
    /// Player: opens the troop editor on a troop and leaves it open, as a player does while making several edits, so the
    /// steps after it run with the editor showing (in Coop, each snapshot then refreshes the open editor).
    /// </summary>
    private static IEnumerable<StepWait?> OpenEditorStep(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        ctx.State[EditorKept] = true;
    }

    /// <summary>Player: closes the troop editor retinues.openEditor left open.</summary>
    private static IEnumerable<StepWait?> CloseEditorStep(StepContext ctx)
    {
        ctx.State.Remove(EditorKept);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
    }

    /// <summary>
    /// Player: raises a retinue's join limit with the editor's raise button, <c>times</c> times, waiting each time until
    /// the editor shows the new limit (a player clicks again only after seeing it). Coop 0.1.7 / Retinues coop.1: the
    /// limit was relayed to the server but never came back, so the editor stayed at 0 and every click asked for 1.
    /// </summary>
    private static IEnumerable<StepWait?> RaiseJoinCap(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var times = ctx.ArgInt("times", 2);
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        var before = AutoJoinBehavior.GetJoinCap(troop);
        for (var i = 1; i <= times; i++)
        {
            ctx.Assert(Editor.Get<bool>("TroopScreen", "CanRaiseRetinueCap"), $"the raise button is off at {AutoJoinBehavior.GetJoinCap(troop)} (max {Editor.Get<int>("TroopScreen", "RetinueCapMax")})");
            Editor.Invoke("TroopScreen", "ExecuteRaiseRetinueCap");
            var want = before + i;
            yield return ctx.Until(() => Editor.Get<int>("TroopScreen", "RetinueCapValue") == want, $"the editor to show a join limit of {want} (shows {Editor.Get<int>("TroopScreen", "RetinueCapValue")})", Sync);
        }
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("before", before);
        ctx.Report("value", AutoJoinBehavior.GetJoinCap(troop));
    }

    // ---- unlocks and stocks -----------------------------------------------------------------------------

    /// <summary>
    /// How many of an item (<c>item</c>) are in stock, settled. Stocks are the campaign's, shared by every player: in
    /// Coop the server holds them and the editor on each player's game quotes prices from them, so a compare checks
    /// both sides agree.
    /// </summary>
    private static IEnumerable<StepWait?> Stock(StepContext ctx)
    {
        var id = ctx.Arg("item");
        if (ctx.HasArg("expected"))
            yield return ctx.Settled(() => StocksBehavior.Get(id), $"the stock of {id}", ctx.ArgInt("expected"), timeout: Sync);
        else
            yield return ctx.Settled(() => StocksBehavior.Get(id), $"the stock of {id}");
        ctx.Report("value", StocksBehavior.Get(id));
    }

    /// <summary>Server: an item of a slot (<c>slot</c>, Head by default) that is not unlocked yet, cheapest first.</summary>
    private static IEnumerable<StepWait?> LockedItem(StepContext ctx)
    {
        var slot = SlotArg(ctx);
        var item = MBObjectManager.Instance.GetObjectTypeList<ItemObject>()
            .Where(i => i.Value > 0 && Equipment.IsItemFitsToSlot(slot, i) && !UnlocksBehavior.IsUnlocked(i.StringId))
            .OrderBy(i => i.Value).ThenBy(i => i.StringId)
            .FirstOrDefault();
        ctx.Assert(item != null, $"every {slot} item is already unlocked");
        ctx.Report("value", item!.StringId);
        yield break;
    }

    /// <summary>
    /// Server: unlocks an item for the campaign, as reaching its kill or discard count does. With <c>announce</c>
    /// true it is also announced, as those unlocks are (a troop's own gear unlocks silently).
    /// </summary>
    private static IEnumerable<StepWait?> Unlock(StepContext ctx)
    {
        var item = ItemArg(ctx);
        UnlocksBehavior.Unlock(item);
        if (ctx.Arg("announce", "false") == "true")
        {
            var queue = typeof(UnlocksBehavior).GetMethod("QueueUnlockNotification", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new StepFailedException("this Retinues has no UnlocksBehavior.QueueUnlockNotification");
            queue.Invoke(UnlocksBehavior.Instance, new object[] { new List<ItemObject> { item } });
        }
        ctx.Report("value", UnlocksBehavior.IsUnlocked(item.StringId));
        yield break;
    }

    /// <summary>
    /// Whether an item (<c>item</c>) is unlocked here, settled; with <c>expected</c>, it must be that. Retinues coop.2:
    /// items unlocked on the server during a session never reached the players' editors.
    /// </summary>
    private static IEnumerable<StepWait?> IsUnlocked(StepContext ctx)
    {
        var id = ctx.Arg("item");
        if (ctx.HasArg("expected"))
            yield return ctx.Settled(() => UnlocksBehavior.IsUnlocked(id), $"whether {id} is unlocked", ctx.Arg("expected") == "true", timeout: Sync);
        else
            yield return ctx.Settled(() => UnlocksBehavior.IsUnlocked(id), $"whether {id} is unlocked");
        ctx.Report("value", UnlocksBehavior.IsUnlocked(id));
    }

    private const string Popups = "retinues.popups";

    /// <summary>
    /// Player: from here on, records the title of every pop-up this game shows (until the run ends), so a later step
    /// can check one appeared while other steps ran.
    /// </summary>
    private static IEnumerable<StepWait?> WatchPopups(StepContext ctx)
    {
        if (!ctx.State.ContainsKey(Popups))
        {
            var seen = new List<InquiryData>();
            ctx.State[Popups] = seen;
            InformationManager.OnShowInquiry += (data, pause, priority) => { lock (seen) seen.Add(data); };
        }
        yield break;
    }

    /// <summary>
    /// Player, Coop: waits for Retinues' "New Gear Unlocked" pop-up (shown on the world map), checks it names
    /// <c>item</c>, and closes it as the player's OK does. Needs retinues.watchPopups earlier. In single player Retinues
    /// shows it from the campaign tick, which a test run's paused time stops, so suites check it in Coop only.
    /// </summary>
    private static IEnumerable<StepWait?> UnlockPopup(StepContext ctx)
    {
        ctx.Assert(Config.UnlockPopup, "Retinues' unlock pop-up is off in its settings (UnlockPopup)");
        var seen = ctx.State.TryGetValue(Popups, out var s) ? (List<InquiryData>)s! : throw new StepFailedException("retinues.watchPopups did not run first");
        var name = ItemArg(ctx).Name.ToString();
        InquiryData? popup = null;
        yield return ctx.Until(() => { lock (seen) popup = seen.LastOrDefault(d => d.TitleText == "New Gear Unlocked"); return popup != null; }, "the New Gear Unlocked pop-up", Sync);
        ctx.Log("pop-up text: " + popup!.Text);
        InformationManager.HideInquiry();
        popup.AffirmativeAction?.Invoke();
        ctx.Assert(popup.Text.Contains(name), $"the pop-up does not name {name}: {popup.Text}");
        ctx.Report("value", popup.Text);
    }

    private static ItemObject ItemArg(StepContext ctx)
    {
        var id = ctx.Arg("item");
        return MBObjectManager.Instance.GetObject<ItemObject>(id) ?? throw new StepFailedException($"no item '{id}'");
    }

    private const string EditorKept = "retinues.editorKept";

    private static IEnumerable<StepWait?> OpenEditor(StepContext ctx, WCharacter troop)
    {
        if (!Editor.IsOpen)
        {
            Editor.Launch();
            yield return ctx.Until(() => Editor.IsOpen, "the troop editor to open", TimeSpan.FromSeconds(20));
        }
        Editor.SelectTroop(troop);
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
    }

    /// <summary>Closes the editor, unless retinues.openEditor asked for it to stay open.</summary>
    private static IEnumerable<StepWait?> CloseEditor(StepContext ctx)
    {
        if (ctx.State.ContainsKey(EditorKept)) yield break;
        var manager = TaleWorlds.Core.Game.Current?.GameStateManager;
        if (manager?.ActiveState is TaleWorlds.CampaignSystem.GameState.ClanState) manager.PopState();
        yield return ctx.Until(() => !(TaleWorlds.Core.Game.Current?.GameStateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.ClanState), "the clan screen to close", TimeSpan.FromSeconds(10));
    }

    /// <summary>Errors and exceptions Retinues wrote to its own log since retinues.health ran (or the whole log).</summary>
    private static IEnumerable<StepWait?> NoErrors(StepContext ctx)
    {
        var start = ctx.State.TryGetValue("retinues.logStart", out var s) && s is long l ? l : 0L;
        var errors = new List<string>();
        if (File.Exists(LogFile))
        {
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (start <= stream.Length) stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Contains("] [Error] ") || line.Contains("] [Critical] ")) errors.Add(line);
        }
        if (errors.Count > 0) ctx.Dump("retinues-errors", string.Join(Environment.NewLine, errors));
        ctx.Assert(errors.Count == 0, $"Retinues logged {errors.Count} error(s) this run; first: {errors.FirstOrDefault()}");
        ctx.Report("value", errors.Count);
        yield break;
    }

    // ---- arguments and helpers -----------------------------------------------------------------------------

    /// <summary>The <c>heroName</c> argument's hero (as game.whoAmI reports it): a player's hero has another StringId on each side.</summary>
    private static Hero HeroArg(StepContext ctx)
    {
        var name = ctx.Arg("heroName");
        var found = Hero.AllAliveHeroes.Where(h => h.Name?.ToString() == name).ToList();
        if (Hero.MainHero != null && Hero.MainHero.Name?.ToString() == name && !found.Contains(Hero.MainHero)) found.Add(Hero.MainHero);
        if (found.Count != 1) throw new StepFailedException(found.Count == 0 ? $"no hero named '{name}' in this world" : $"{found.Count} heroes are named '{name}'");
        return found[0];
    }

    private static WCharacter TroopArg(StepContext ctx)
    {
        var id = ctx.Arg("troop");
        var troop = new WCharacter(id);
        if (troop.Base == null) throw new StepFailedException($"no troop '{id}' in this game");
        return troop;
    }

    private static EquipmentIndex SlotArg(StepContext ctx)
    {
        var name = ctx.Arg("slot", nameof(EquipmentIndex.Head));
        return Enum.TryParse<EquipmentIndex>(name, out var slot) ? slot : throw new StepFailedException($"no equipment slot '{name}'");
    }

    /// <summary>A troop and everything it upgrades into, breadth first.</summary>
    private static IEnumerable<WCharacter> Tree(WCharacter root)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<WCharacter>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var troop = queue.Dequeue();
            if (!seen.Add(troop.StringId)) continue;
            yield return troop;
            foreach (var next in troop.UpgradeTargets) queue.Enqueue(next);
        }
    }

    /// <summary>Items this troop may equip in a slot of its battle set, cheapest first.</summary>
    private static IEnumerable<WItem> Candidates(WCharacter troop, EquipmentIndex slot) =>
        MBObjectManager.Instance.GetObjectTypeList<ItemObject>()
            .Where(i => i.Value > 0 && Equipment.IsItemFitsToSlot(slot, i))
            .OrderBy(i => i.Value).ThenBy(i => i.StringId)
            .Select(i => new WItem(i.StringId))
            .Where(w => EquipmentManager.CanEquip(troop, w));

    private static string Describe(WCharacter troop)
    {
        var sb = new StringBuilder();
        sb.Append(troop.StringId).Append(" '").Append(troop.Name).Append('\'');
        sb.Append(" skills:").Append(string.Join(",", MBObjectManager.Instance.GetObjectTypeList<SkillObject>().OrderBy(s => s.StringId).Select(s => $"{s.StringId}={troop.GetSkill(s)}")));
        var set = troop.Loadout.Get(0);
        sb.Append(" battle:").Append(string.Join(",", Enumerable.Range(0, (int)EquipmentIndex.NumEquipmentSetSlots)
            .Select(i => set.Get((EquipmentIndex)i)?.Base?.StringId ?? "-")));
        sb.Append(" upgrades:").Append(string.Join(",", troop.UpgradeTargets.Select(t => t.StringId).OrderBy(x => x)));
        if (troop.IsRetinue) sb.Append(" joinLimit:").Append(AutoJoinBehavior.GetJoinCap(troop));
        return sb.ToString();
    }

    private static string LogFile
    {
        get
        {
            // Retinues writes debug.log in its module folder, two levels above its DLL.
            var bin = Path.GetDirectoryName(typeof(Player).Assembly.Location)!;
            return Path.Combine(Directory.GetParent(bin)!.Parent!.FullName, "debug.log");
        }
    }

    /// <summary>The parts of Retinues only its Coop fork has, by reflection.</summary>
    private static class Fork
    {
        private static Type? Bridge => typeof(Player).Assembly.GetType("Retinues.Mods.Coop.CoopBridge", false);
        private static Type? State => typeof(Player).Assembly.GetType("Retinues.Mods.Coop.CoopState", false);

        public static bool HasCoopSupport => Bridge != null;
        public static bool InSession => Read(State, "InSession");
        public static bool IsClient => Read(State, "IsClient");
        public static bool Ready => Read(Bridge, "Ready");

        public static void SendAppearanceIfChanged(WCharacter troop) =>
            Bridge?.GetMethod("SendAppearanceIfChanged", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { troop });

        private static bool Read(Type? type, string property) =>
            type?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
    }

    /// <summary>Retinues' troop editor (a tab of the clan screen), by reflection: its screen types need UIExtenderEx to compile.</summary>
    private static class Editor
    {
        private static Assembly Retinues => typeof(Player).Assembly;
        private static Type ClanScreen => Retinues.GetType("Retinues.GUI.Editor.ClanScreen", true)!;
        private static object? Vm => ClanScreen.GetProperty("Instance")?.GetValue(null) is { } screen ? screen.GetType().GetProperty("Editor")?.GetValue(screen) : null;

        public static bool IsOpen => Vm != null && TaleWorlds.Core.Game.Current?.GameStateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.ClanState;

        /// <summary>The player's own troops (EditorMode.Personal), as the clan screen's Troops tab opens them.</summary>
        public static void Launch()
        {
            var mode = Enum.Parse(Retinues.GetType("Retinues.GUI.Editor.EditorMode", true)!, "Personal");
            ClanScreen.GetMethod("LaunchEditor")!.Invoke(null, new[] { mode });
        }

        /// <summary>Picks a troop, as clicking it in the editor's troop list does.</summary>
        public static void SelectTroop(WCharacter troop) =>
            Retinues.GetType("Retinues.GUI.Editor.State", true)!.GetMethod("UpdateTroop")!.Invoke(null, new object[] { troop });

        public static void SwitchScreen(string screen)
        {
            var vm = Vm ?? throw new StepFailedException("the troop editor is not open");
            var type = Retinues.GetType("Retinues.GUI.Editor.VM.Screen", true)!;
            vm.GetType().GetMethod("SwitchScreen")!.Invoke(vm, new[] { Enum.Parse(type, screen) });
        }

        /// <summary>Reads a property of one of the editor's pages, as its screen binds it.</summary>
        public static T Get<T>(string page, string property)
        {
            var vm = Vm ?? throw new StepFailedException("the troop editor is not open");
            var screen = vm.GetType().GetProperty(page)?.GetValue(vm) ?? throw new StepFailedException($"the editor has no {page}");
            return (T)(screen.GetType().GetProperty(property)?.GetValue(screen) ?? throw new StepFailedException($"{page} has no {property}"));
        }

        /// <summary>Calls a button's method on one of the editor's pages (TroopScreen, EquipmentScreen).</summary>
        public static void Invoke(string page, string method)
        {
            var vm = Vm ?? throw new StepFailedException("the troop editor is not open");
            var screen = vm.GetType().GetProperty(page)?.GetValue(vm) ?? throw new StepFailedException($"the editor has no {page}");
            screen.GetType().GetMethod(method)!.Invoke(screen, null);
        }
    }
}
