using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using Retinues.Configuration;
using Retinues.Doctrines;
using Retinues.Features.Experience;
using Retinues.Features.Staging;
using Retinues.Game;
using Retinues.Game.Wrappers;
using Retinues.Managers;
using Retinues.Troops;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Tests.Retinues;

/// <summary>
/// Steps for the rest of Retinues' features: appearance, skill caps, equipment sets, retinue conversion and hiring,
/// special troops, doctrines, the global editor, edits that take time, import and export, and volunteers. As in
/// Steps.cs, each calls what the editor's button or the game's menu calls, so in Coop it takes the player's path.
/// </summary>
public static partial class Steps
{
    internal static void RegisterFeatures()
    {
        InGameTests.Register("retinues.makeRuler", StepSide.Server, MakeRuler);
        InGameTests.Register("retinues.toggleGender", StepSide.Client, ToggleGender);
        InGameTests.Register("retinues.pickCulture", StepSide.Client, PickCulture);
        InGameTests.Register("retinues.pickFormation", StepSide.Client, PickFormation);
        InGameTests.Register("retinues.skillToCap", StepSide.Client, SkillToCap);
        InGameTests.Register("retinues.skillDown", StepSide.Client, SkillDown);
        InGameTests.Register("retinues.skillRules", StepSide.Any, SkillRules);
        InGameTests.Register("retinues.fillUpgrades", StepSide.Client, FillUpgrades);
        InGameTests.Register("retinues.createSet", StepSide.Client, CreateSet);
        InGameTests.Register("retinues.deleteSet", StepSide.Client, DeleteSet);
        InGameTests.Register("retinues.sets", StepSide.Any, Sets);
        InGameTests.Register("retinues.paste", StepSide.Client, Paste);
        InGameTests.Register("retinues.tooHighTier", StepSide.Client, TooHighTier);
        InGameTests.Register("retinues.addTroops", StepSide.Server, AddTroops);
        InGameTests.Register("retinues.partyCount", StepSide.Any, PartyCount);
        InGameTests.Register("retinues.convert", StepSide.Client, ConvertStep);
        InGameTests.Register("retinues.rankUp", StepSide.Client, RankUpStep);
        InGameTests.Register("retinues.addRenown", StepSide.Server, AddRenown);
        InGameTests.Register("retinues.passDays", StepSide.Server, PassDays);
        InGameTests.Register("retinues.specialTroops", StepSide.Any, SpecialTroops);
        InGameTests.Register("retinues.xpPool", StepSide.Any, XpPool);
        InGameTests.Register("retinues.acquireDoctrine", StepSide.Client, AcquireDoctrine);
        InGameTests.Register("retinues.captain", StepSide.Client, CaptainStep);
        InGameTests.Register("retinues.openGlobalEditor", StepSide.Client, OpenGlobalEditor);
        InGameTests.Register("retinues.cultureTroop", StepSide.Any, CultureTroop);
        InGameTests.Register("retinues.renameVanilla", StepSide.Client, RenameVanilla);
        InGameTests.Register("retinues.staged", StepSide.Client, Staged);
        InGameTests.Register("retinues.toTown", StepSide.Server, ToTown);
        InGameTests.Register("retinues.trainInTown", StepSide.Client, TrainInTown);
        InGameTests.Register("retinues.exportTroops", StepSide.Client, ExportTroops);
        InGameTests.Register("retinues.importTroops", StepSide.Client, ImportTroops);
        InGameTests.Register("retinues.askExport", StepSide.Client, AskExport);
        InGameTests.Register("retinues.volunteers", StepSide.Any, Volunteers);
        InGameTests.Register("retinues.addInfluence", StepSide.Server, AddInfluence);
        InGameTests.Register("retinues.retinueSource", StepSide.Client, RetinueSource);
    }

    // ---- kingdom -------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: makes a hero's clan the founder and ruler of a new kingdom, as the game's own "create a kingdom" does
    /// (KingdomManager.CreateKingdom), so Retinues gives it a kingdom troop tree. Reports the kingdom's id.
    /// </summary>
    private static IEnumerable<StepWait?> MakeRuler(StepContext ctx)
    {
        var hero = HeroArg(ctx);
        var clan = hero.Clan ?? throw new StepFailedException("the hero has no clan");
        // A new kingdom asks the same copy-or-scratch question as a clan's first fief (single player only). Left open it
        // stays queued through world reloads and paused every later suite in run 4 (2026-11-22).
        InquiryData? asked = null;
        void Capture(InquiryData data, bool pause, bool priority) { if (data.TitleText == "Custom Troops Unlocked") asked = data; }
        InformationManager.OnShowInquiry += Capture;
        try
        {
        if (clan.Kingdom == null || clan.Kingdom.RulingClan != clan)
        {
            var manager = Campaign.Current.KingdomManager;
            var create = manager.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateKingdom")
                ?? throw new StepFailedException("this game has no KingdomManager.CreateKingdom");
            var name = new TextObject(ctx.Arg("name", "Harness Realm"));
            var args = create.GetParameters().Select(p => p.ParameterType == typeof(TextObject) ? name
                : p.ParameterType == typeof(CultureObject) ? clan.Culture
                : p.ParameterType == typeof(Clan) ? clan
                : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            create.Invoke(manager, args);
        }
        yield return ctx.Until(() => clan.Kingdom != null && clan.Kingdom.RulingClan == clan, $"{clan.Name} to rule a kingdom", Sync);
        var until = DateTime.UtcNow + (Fork.InSession ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30));
        while (asked == null && DateTime.UtcNow < until) yield return null;
        }
        finally
        {
            InformationManager.OnShowInquiry -= Capture;
        }
        if (asked != null)
        {
            foreach (var wait in UntilShown(ctx, asked)) yield return wait;
            ctx.Log($"answered '{asked.TitleText}' with '{asked.AffirmativeText}'");
            InformationManager.HideInquiry();
            asked.AffirmativeAction?.Invoke();
        }
        ctx.Report("value", clan.Kingdom!.StringId);
    }

    // ---- appearance ----------------------------------------------------------------------------------------

    /// <summary>Player: the troop panel's gender button, with the editor on <c>troop</c>.</summary>
    private static IEnumerable<StepWait?> ToggleGender(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        var before = troop.IsFemale;
        Editor.Invoke("TroopScreen", "ExecuteChangeGender");
        Fork.SendAppearanceIfChanged(troop);
        yield return ctx.Settled(() => troop.IsFemale, $"{troop.StringId}'s gender", !before, timeout: Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", troop.IsFemale ? "female" : "male");
    }

    /// <summary>
    /// Player: the troop panel's culture button, answering its picker with <c>culture</c> (a culture id), as a player's
    /// pick and Confirm do.
    /// </summary>
    private static IEnumerable<StepWait?> PickCulture(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var id = ctx.Arg("culture");
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        foreach (var wait in PickFromMulti(ctx, () => Editor.Invoke("TroopScreen.TroopPanel", "ExecuteChangeCulture"), e => (e.Identifier as CultureObject)?.StringId == id, $"culture {id}")) yield return wait;
        Fork.SendAppearanceIfChanged(troop);
        yield return ctx.Settled(() => troop.Culture?.StringId, $"{troop.StringId}'s culture", id, timeout: Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", troop.Culture?.StringId);
    }

    /// <summary>
    /// Player: the troop panel's formation button (needs Retinues' AllowFormationOverrides), picking <c>formation</c>
    /// (a FormationClass name).
    /// </summary>
    private static IEnumerable<StepWait?> PickFormation(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var want = (FormationClass)Enum.Parse(typeof(FormationClass), ctx.Arg("formation"));
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        foreach (var wait in PickFromMulti(ctx, () => Editor.Invoke("TroopScreen.TroopPanel", "ExecuteChangeFormationClass"), e => e.Identifier is FormationClass f && f == want, $"formation {want}")) yield return wait;
        Fork.SendAppearanceIfChanged(troop);
        yield return ctx.Settled(() => troop.FormationClassOverride, $"{troop.StringId}'s formation", want, timeout: Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", troop.FormationClassOverride.ToString());
    }

    // ---- skills and upgrades -------------------------------------------------------------------------------

    /// <summary>
    /// Player: raises one skill with the skill arrow until the editor refuses, then checks it stopped at the tier's cap
    /// (or the tier's skill total). Reports the value reached and the refusal.
    /// </summary>
    private static IEnumerable<StepWait?> SkillToCap(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var skill = SkillArg(ctx);
        var cap = SkillManager.SkillCapByTier(troop);
        for (var guard = 0; guard < 400; guard++)
        {
            yield return ctx.Settled(() => troop.GetSkill(skill) + "/" + TroopXpBehavior.Get(troop), $"{troop.StringId}'s {skill.StringId} and XP");
            if (!SkillManager.CanIncrementSkill(troop, skill)) break;
            var before = troop.GetSkill(skill);
            SkillManager.ModifySkill(troop, skill, increment: true);
            yield return ctx.Until(() => troop.GetSkill(skill) == before + 1, $"{troop.StringId}'s {skill.StringId} to reach {before + 1}", Sync);
        }
        var value = troop.GetSkill(skill);
        var reason = SkillManager.GetIncrementSkillReason(troop, skill)?.ToString() ?? "";
        ctx.Log($"{skill.StringId} stopped at {value} (cap {cap}, points left {SkillManager.SkillPointsLeft(troop)}, XP {TroopXpBehavior.Get(troop)}): {reason}");
        ctx.Assert(value <= cap, $"{skill.StringId} went to {value}, over the tier cap {cap}");
        ctx.Assert(value == cap || SkillManager.SkillPointsLeft(troop) == 0, $"{skill.StringId} stopped at {value}, below the cap {cap} with {SkillManager.SkillPointsLeft(troop)} points left: {reason}");
        ctx.Report("cap", cap);
        ctx.Report("reason", reason);
        ctx.Report("value", value);
    }

    /// <summary>Player: lowers a skill a point with the skill arrow; the XP it cost comes back (a refund).</summary>
    private static IEnumerable<StepWait?> SkillDown(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var skill = SkillArg(ctx);
        ctx.Assert(SkillManager.CanDecrementSkill(troop, skill), $"the editor will not lower {skill.StringId}: {SkillManager.GetDecrementSkillReason(troop, skill)}");
        var before = troop.GetSkill(skill);
        var xp = TroopXpBehavior.Get(troop);
        SkillManager.ModifySkill(troop, skill, increment: false);
        yield return ctx.Settled(() => troop.GetSkill(skill), $"{troop.StringId}'s {skill.StringId}", before - 1, timeout: Sync);
        yield return ctx.Settled(() => TroopXpBehavior.Get(troop), $"{troop.StringId}'s XP");
        ctx.Log($"XP {xp} -> {TroopXpBehavior.Get(troop)}");
        if (ctx.Arg("refund", "true") == "true")
            ctx.Assert(TroopXpBehavior.Get(troop) > xp, $"lowering {skill.StringId} refunded nothing (XP {xp} -> {TroopXpBehavior.Get(troop)})");
        ctx.Report("value", troop.GetSkill(skill));
    }

    /// <summary>A troop's skill cap and total for its tier, and its XP pool, as this game computes them.</summary>
    private static IEnumerable<StepWait?> SkillRules(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        yield return ctx.Settled(() => $"{SkillManager.SkillCapByTier(troop)}/{SkillManager.SkillTotalByTier(troop)}/{TroopXpBehavior.Get(troop)}", $"{troop.StringId}'s skill rules");
        ctx.Report("cap", SkillManager.SkillCapByTier(troop));
        ctx.Report("total", SkillManager.SkillTotalByTier(troop));
        ctx.Report("xp", TroopXpBehavior.Get(troop));
        if (ctx.HasArg("capAbove"))
            ctx.Assert(SkillManager.SkillCapByTier(troop) > ctx.ArgInt("capAbove"), $"the skill cap is {SkillManager.SkillCapByTier(troop)}, expected above {ctx.ArgInt("capAbove")}");
        ctx.Report("value", $"cap={SkillManager.SkillCapByTier(troop)} total={SkillManager.SkillTotalByTier(troop)}");
    }

    /// <summary>
    /// Player: adds upgrades to a troop until the editor's add-upgrade button refuses, and checks it stopped at
    /// Retinues' MaxBasicUpgrades (MaxEliteUpgrades for an elite troop).
    /// </summary>
    private static IEnumerable<StepWait?> FillUpgrades(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var max = troop.IsElite ? Config.MaxEliteUpgrades : Config.MaxBasicUpgrades;
        for (var i = 0; i < 6 && UpgradeManager.CanAddUpgradeToTroop(troop); i++)
        {
            var count = troop.UpgradeTargets.Count();
            UpgradeManager.AddUpgradeTarget(troop, $"Harness Branch {i + 1}");
            yield return ctx.Until(() => troop.UpgradeTargets.Count() == count + 1, $"{troop.StringId} to have {count + 1} upgrades", Sync);
        }
        var reason = UpgradeManager.GetAddUpgradeToTroopReason(troop)?.ToString() ?? "";
        var have = troop.UpgradeTargets.Count();
        ctx.Log($"{troop.StringId} has {have} upgrades (max {max}): {reason}");
        ctx.Assert(have == max, $"{troop.StringId} stopped at {have} upgrades, expected the limit {max}: {reason}");
        ctx.Report("value", have);
    }

    // ---- equipment -----------------------------------------------------------------------------------------

    /// <summary>
    /// Player: the equipment page's create-set button, answering its pop-up with Copy Current (<c>copy</c> true,
    /// the default) or Empty. Reports the new set count.
    /// </summary>
    private static IEnumerable<StepWait?> CreateSet(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var before = troop.Loadout.Equipments.Count;
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        Editor.SwitchScreen("Equipment");
        yield return null;
        var copy = ctx.Arg("copy", "true") == "true";
        foreach (var wait in Answer(ctx, () => Editor.Invoke("EquipmentScreen", "ExecuteCreateSet"), copy)) yield return wait;
        yield return ctx.Settled(() => troop.Loadout.Equipments.Count, $"{troop.StringId}'s set count", before + 1, timeout: Sync);
        Editor.SwitchScreen("Troop");
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("index", troop.Loadout.Equipments.Count - 1);
        ctx.Report("value", troop.Loadout.Equipments.Count);
    }

    /// <summary>Player: deletes a set (<c>index</c>) as the set's delete button does.</summary>
    private static IEnumerable<StepWait?> DeleteSet(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var index = ctx.ArgInt("index");
        var before = troop.Loadout.Equipments.Count;
        var result = EquipmentManager.TryDeleteSet(troop, index);
        ctx.Assert(result.Ok, $"TryDeleteSet refused set {index}");
        yield return ctx.Settled(() => troop.Loadout.Equipments.Count, $"{troop.StringId}'s set count", before - 1, timeout: Sync);
        ctx.Report("value", troop.Loadout.Equipments.Count);
    }

    /// <summary>A troop's equipment sets (count, battle or civilian, and each set's items), settled, for compares.</summary>
    private static IEnumerable<StepWait?> Sets(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        string Text() => string.Join(" | ", troop.Loadout.Equipments.Select((e, i) => $"{i}{(e.IsCivilian ? "c" : "b")}:" +
            string.Join(",", WEquipment.Slots.Select(s => e.Get(s)?.Base?.StringId ?? "-"))));
        if (ctx.HasArg("count"))
            yield return ctx.Settled(() => troop.Loadout.Equipments.Count, $"{troop.StringId}'s set count", ctx.ArgInt("count"), timeout: Sync);
        yield return ctx.Settled(Text, $"{troop.StringId}'s sets");
        ctx.Dump("sets", Text());
        ctx.Report("count", troop.Loadout.Equipments.Count);
        ctx.Report("value", Text());
    }

    /// <summary>
    /// Player: copies <c>from</c>'s battle set onto <c>troop</c>'s, as the equipment page's copy and paste do, and waits
    /// for every slot to match.
    /// </summary>
    private static IEnumerable<StepWait?> Paste(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var source = new WCharacter(ctx.Arg("from"));
        string Items(WCharacter t) => string.Join(",", WEquipment.Slots.Select(s => t.Loadout.Get(0).Get(s)?.Base?.StringId ?? "-"));
        var result = EquipmentManager.TryPasteEquipment(source.Loadout.Get(0), troop.Loadout.Get(0));
        ctx.Assert(result.Ok, $"TryPasteEquipment refused: {result.Reason} {result.Details}");
        yield return ctx.Settled(() => Items(troop), $"{troop.StringId}'s battle set", Items(source), timeout: Sync);
        ctx.Report("value", Items(troop));
    }

    /// <summary>
    /// Player: picks an item the troop's tier may not wear yet (Retinues' AllowedTierDifference) and checks the editor
    /// refuses it and the slot stays as it was.
    /// </summary>
    private static IEnumerable<StepWait?> TooHighTier(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var slot = SlotArg(ctx);
        var item = MBObjectManager.Instance.GetObjectTypeList<ItemObject>()
            .Where(i => i.Value > 0 && Equipment.IsItemFitsToSlot(slot, i))
            .OrderByDescending(i => i.Tier).ThenBy(i => i.StringId)
            .Select(i => new WItem(i.StringId))
            .FirstOrDefault(w => !EquipmentManager.CanEquip(troop, w, out var why) && why.ToString().IndexOf("Tier", StringComparison.OrdinalIgnoreCase) >= 0);
        ctx.Assert(item != null, $"no {slot} item is refused for {troop.StringId}'s tier {troop.Tier} (AllowedTierDifference {Config.AllowedTierDifference})");
        EquipmentManager.CanEquip(troop, item!, out var reasons);
        var before = troop.Loadout.Get(0).Get(slot)?.Base?.StringId ?? "";
        var gold = Player.Gold;
        var result = EquipmentManager.TryEquip(troop, 0, slot, item!);
        ctx.Assert(!result.Ok, $"TryEquip took {item!.Base.StringId} (tier {item.Base.Tier}) for a tier {troop.Tier} troop");
        yield return ctx.Delay(TimeSpan.FromSeconds(2));
        ctx.Assert((troop.Loadout.Get(0).Get(slot)?.Base?.StringId ?? "") == before, $"{slot} changed after a refused equip");
        ctx.Assert(Player.Gold == gold, $"a refused equip took gold ({gold} -> {Player.Gold})");
        ctx.Report("value", $"{item!.Base.StringId}: {reasons}");
    }

    // ---- retinues ------------------------------------------------------------------------------------------

    /// <summary>Server: adds <c>count</c> of a troop (<c>troop</c>) to a hero's party.</summary>
    private static IEnumerable<StepWait?> AddTroops(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var troop = TroopArg(ctx);
        party.MemberRoster.AddToCounts(troop.Base, ctx.ArgInt("count"));
        ctx.Report("value", party.MemberRoster.GetTroopRoster().Where(e => e.Character == troop.Base).Sum(e => e.Number));
        yield break;
    }

    /// <summary>How many of a troop (<c>troop</c>) are in a hero's party, settled; with <c>expected</c>, that many.</summary>
    private static IEnumerable<StepWait?> PartyCount(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var troop = TroopArg(ctx);
        int Count() => party.MemberRoster.GetTroopRoster().Where(e => e.Character == troop.Base).Sum(e => e.Number);
        if (ctx.HasArg("expected"))
            yield return ctx.Settled(() => Count(), $"{troop.StringId} in the party", ctx.ArgInt("expected"), timeout: Sync);
        else if (ctx.HasArg("atLeast"))
        {
            yield return ctx.Until(() => Count() >= ctx.ArgInt("atLeast"), $"at least {ctx.ArgInt("atLeast")} {troop.StringId} in the party (has {Count()})", Sync);
            yield return ctx.Settled(() => Count(), $"{troop.StringId} in the party");
        }
        else
            yield return ctx.Settled(() => Count(), $"{troop.StringId} in the party");
        ctx.Report("value", Count());
    }

    /// <summary>
    /// Player: converts <c>count</c> of <c>from</c> into the retinue <c>troop</c>, as the conversion row's buttons and
    /// Apply do, and checks the gold and influence it cost. Reports how many were converted.
    /// </summary>
    private static IEnumerable<StepWait?> ConvertStep(StepContext ctx)
    {
        var retinue = TroopArg(ctx);
        var from = new WCharacter(ctx.Arg("from"));
        var count = ctx.ArgInt("count");
        var max = RetinueManager.GetMaxConvertible(from, retinue);
        var amount = Math.Min(count, max);
        var gold = Player.Gold;
        var influence = Player.Influence;
        var goldCost = RetinueManager.ConversionGoldCostPerUnit(retinue) * amount;
        var party = MobileParty.MainParty.MemberRoster;
        int Have() => party.GetTroopRoster().Where(e => e.Character == retinue.Base).Sum(e => e.Number);
        var before = Have();
        ctx.Log($"converting {count} {from.StringId} -> {retinue.StringId} (max {max}, cap {RetinueManager.RetinueCapFor(retinue)}, {goldCost} gold)");
        RetinueManager.Convert(from, retinue, count);
        yield return ctx.Settled(() => Have(), $"{retinue.StringId} in the party", before + amount, timeout: Sync);
        yield return ctx.Settled(() => Player.Gold, "the player's gold", gold - goldCost, timeout: Sync);
        ctx.Log($"influence {influence} -> {Player.Influence}");
        ctx.Report("max", max);
        ctx.Report("value", Have() - before);
    }

    /// <summary>Player: the retinue's rank-up button; the troop goes up a tier and the gold is taken.</summary>
    private static IEnumerable<StepWait?> RankUpStep(StepContext ctx)
    {
        var retinue = TroopArg(ctx);
        var tier = retinue.Tier;
        var gold = Player.Gold;
        var cost = RetinueManager.RankUpCost(retinue);
        RetinueManager.RankUp(retinue);
        yield return ctx.Settled(() => retinue.Tier, $"{retinue.StringId}'s tier", tier + 1, timeout: Sync);
        yield return ctx.Settled(() => Player.Gold, "the player's gold", gold - cost, timeout: Sync);
        ctx.Report("value", retinue.Tier);
    }

    /// <summary>Server: gives a hero's clan renown (<c>amount</c>), as winning battles does.</summary>
    private static IEnumerable<StepWait?> AddRenown(StepContext ctx)
    {
        var clan = HeroArg(ctx).Clan;
        clan.AddRenown(ctx.ArgInt("amount"), true);
        ctx.Report("value", clan.Renown);
        yield break;
    }

    /// <summary>Server: gives a hero's clan influence (<c>amount</c>), which retinue conversions cost.</summary>
    private static IEnumerable<StepWait?> AddInfluence(StepContext ctx)
    {
        var clan = HeroArg(ctx).Clan;
        ChangeClanInfluenceAction.Apply(clan, ctx.ArgInt("amount"));
        ctx.Report("value", clan.Influence);
        yield break;
    }

    /// <summary>
    /// Player: the troop the conversion rows offer to turn into a retinue (<c>troop</c>): the best match from the clan's
    /// own or its culture's tree. Reports its id.
    /// </summary>
    private static IEnumerable<StepWait?> RetinueSource(StepContext ctx)
    {
        var retinue = TroopArg(ctx);
        var source = RetinueManager.GetRetinueSourceTroops(retinue).FirstOrDefault()
            ?? throw new StepFailedException($"no troop converts into {retinue.StringId}");
        ctx.Report("value", source.StringId);
        yield break;
    }

    /// <summary>
    /// Server: lets campaign time run until <c>days</c> days have passed, then stops it again. In Coop through Coop's own
    /// time command (setting the mode here does not reach Coop's clock).
    /// </summary>
    private static IEnumerable<StepWait?> PassDays(StepContext ctx)
    {
        var days = ctx.ArgInt("days", 1);
        var start = CampaignTime.Now;
        var coop = CommandLineFunctionality.HasFunctionForCommand("coop.debug.set_time_mode");
        void Set(string mode, CampaignTimeControlMode local)
        {
            if (coop) ctx.Log(CommandLineFunctionality.CallFunction("coop.debug.set_time_mode", new List<string> { mode }, out _));
            else Campaign.Current.TimeControlMode = local;
        }
        // Unstoppable, as the game's own wait menus run it: the stoppable modes stand still while the player's party
        // holds still (Campaign.TickMapTime: IsMainPartyWaiting), and a test party never moves.
        Set("Play_2x", CampaignTimeControlMode.UnstoppableFastForward);
        try
        {
            yield return ctx.Until(() =>
            {
                if (!coop && Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop)
                    Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppableFastForward;
                return (CampaignTime.Now - start).ToDays >= days;
            }, $"{days} day(s) to pass", TimeSpan.FromSeconds(60 + 120 * days));
        }
        finally
        {
            Set("Pause", CampaignTimeControlMode.Stop);
        }
        ctx.Report("value", CampaignTime.Now.ToString());
    }

    // ---- special troops ------------------------------------------------------------------------------------

    /// <summary>
    /// The player clan's special troops (militia, caravan guard and master, veteran caravan guard, villager), which
    /// Retinues makes with their doctrines or NoDoctrineRequirements. Reports each id; fails on a missing one.
    /// </summary>
    private static IEnumerable<StepWait?> SpecialTroops(StepContext ctx)
    {
        // On the server, Player is the server's own hero: name the player's (heroName) to read their clan there.
        var clan = (ctx.HasArg("heroName") ? WFaction.Get(HeroArg(ctx).Clan) : Player.Clan) ?? throw new StepFailedException("the player has no clan");
        (string Name, Func<WCharacter?> Get)[] roots =
        {
            ("militiaMelee", () => clan.MilitiaMelee), ("militiaMeleeElite", () => clan.MilitiaMeleeElite),
            ("militiaRanged", () => clan.MilitiaRanged), ("militiaRangedElite", () => clan.MilitiaRangedElite),
            ("caravanGuard", () => clan.CaravanGuard), ("veteranCaravanGuard", () => clan.VeteranCaravanGuard),
            ("caravanMaster", () => clan.CaravanMaster), ("villager", () => clan.Villager),
        };
        yield return ctx.Until(() => roots.All(r => r.Get() != null), "every special troop: " + string.Join(", ", roots.Where(r => r.Get() == null).Select(r => r.Name)), Sync);
        foreach (var (name, get) in roots) ctx.Report(name, get()!.StringId);
        ctx.Report("value", string.Join(",", roots.Select(r => r.Get()!.StringId)));
    }

    /// <summary>A troop's XP pool (Retinues' XP to spend on skills), settled; with <c>above</c>, more than that.</summary>
    private static IEnumerable<StepWait?> XpPool(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        if (ctx.HasArg("above"))
            yield return ctx.Until(() => TroopXpBehavior.Get(troop) > ctx.ArgInt("above"), $"{troop.StringId}'s XP above {ctx.Arg("above")} (has {TroopXpBehavior.Get(troop)})", Sync);
        yield return ctx.Settled(() => TroopXpBehavior.Get(troop), $"{troop.StringId}'s XP");
        ctx.Report("value", TroopXpBehavior.Get(troop));
    }

    // ---- doctrines -----------------------------------------------------------------------------------------

    /// <summary>
    /// Player: completes a doctrine's feats (and its prerequisites') and buys it with the doctrine screen's button, as a
    /// player who earned it would; gold and influence are given first. <c>doctrine</c> is its class name
    /// (IronDiscipline).
    /// </summary>
    private static IEnumerable<StepWait?> AcquireDoctrine(StepContext ctx)
    {
        yield return ctx.Until(() => DoctrineAPI.AllDoctrines().Count > 0, "Retinues' doctrine catalog", Sync);
        var wanted = DoctrineAPI.AllDoctrines().FirstOrDefault(d => d.Key.EndsWith("." + ctx.Arg("doctrine"), StringComparison.Ordinal))
            ?? throw new StepFailedException($"no doctrine '{ctx.Arg("doctrine")}' (have {string.Join(", ", DoctrineAPI.AllDoctrines().Select(d => d.Key))})");
        var chain = new List<DoctrineDefinition>();
        for (var d = wanted; d != null; d = string.IsNullOrEmpty(d.PrerequisiteKey) ? null : DoctrineAPI.GetDoctrine(d.PrerequisiteKey))
            chain.Insert(0, d);
        foreach (var d in chain)
        {
            if (DoctrineAPI.IsDoctrineUnlocked(d.Key)) continue;
            foreach (var feat in d.Feats) DoctrineAPI.SetFeatProgress(feat.Key, feat.Target);
            GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, d.GoldCost + 1000, true);
            ChangeClanInfluenceAction.Apply(Clan.PlayerClan, d.InfluenceCost + 100);
            ctx.Assert(DoctrineAPI.TryAcquireDoctrine(d.Key, out var reason), $"could not buy {d.Key}: {reason} (status {DoctrineAPI.GetDoctrineStatus(d.Key)})");
            ctx.Log($"bought {d.Key}");
        }
        yield return ctx.Until(() => DoctrineAPI.IsDoctrineUnlocked(wanted.Key), $"{wanted.Key} to be unlocked", Sync);
        ctx.Report("value", wanted.Key);
    }

    /// <summary>
    /// Player: the editor's Captain Mode on <c>troop</c> (needs the Captains doctrine): the troop gets a captain, and
    /// switching its captain on sticks.
    /// </summary>
    private static IEnumerable<StepWait?> CaptainStep(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        ctx.Assert(troop.CanHaveCaptain, $"{troop.StringId} cannot have a captain");
        foreach (var wait in OpenEditor(ctx, troop)) yield return wait;
        var vm = Editor.Vm ?? throw new StepFailedException("the troop editor is not open");
        vm.GetType().GetMethod("ExecuteToggleCaptainMode")!.Invoke(vm, null);
        yield return ctx.Until(() => troop.Captain != null, $"{troop.StringId}'s captain", Sync);
        troop.CaptainEnabled = true;
        yield return ctx.Settled(() => troop.CaptainEnabled, $"{troop.StringId}'s captain switch", true, timeout: Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", troop.Captain!.StringId);
    }

    // ---- the global editor ---------------------------------------------------------------------------------

    /// <summary>
    /// Player: opens the global editor (culture troops), as its link in the troop editor does, and checks it shows; with
    /// <c>expected</c> "off", checks it stays closed (Coop turns it off).
    /// </summary>
    private static IEnumerable<StepWait?> OpenGlobalEditor(StepContext ctx)
    {
        var off = ctx.Arg("expected", "on") == "off";
        if (off)
        {
            ctx.Assert(!Config.EnableGlobalEditor, "the global editor is on here");
            ctx.Report("value", "off");
            yield break;
        }
        Editor.Launch("Culture");
        yield return ctx.Until(() => Editor.IsOpen, "the global editor to open", TimeSpan.FromSeconds(20));
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", "on");
    }

    /// <summary>The player's culture's basic root troop, a vanilla troop the global editor edits. Reports its id.</summary>
    private static IEnumerable<StepWait?> CultureTroop(StepContext ctx)
    {
        var culture = new WCulture(Hero.MainHero.Culture);
        var root = culture.RootBasic ?? throw new StepFailedException($"{culture.StringId} has no basic root troop");
        ctx.Report("name", root.Name);
        ctx.Report("value", root.StringId);
        yield break;
    }

    /// <summary>Player: renames a vanilla troop (<c>troop</c>) in the global editor, which keeps the edit in the save.</summary>
    private static IEnumerable<StepWait?> RenameVanilla(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        Editor.Launch("Culture");
        yield return ctx.Until(() => Editor.IsOpen, "the global editor to open", TimeSpan.FromSeconds(20));
        Editor.SelectTroop(troop);
        yield return null;
        troop.Name = ctx.Arg("name");
        troop.NeedsPersistence = true;
        yield return ctx.Settled(() => troop.Name, $"{troop.StringId}'s name", ctx.Arg("name"), timeout: Sync);
        foreach (var wait in CloseEditor(ctx)) yield return wait;
        ctx.Report("value", troop.Name);
    }

    // ---- edits that take time ------------------------------------------------------------------------------

    /// <summary>
    /// Player: a troop's staged training (TrainingTroopsTakesTime): how many skill points wait for training in a fief.
    /// With <c>expected</c>, that many.
    /// </summary>
    private static IEnumerable<StepWait?> Staged(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        int Count() => TrainStagingBehavior.Get(troop)?.Sum(p => p.PointsRemaining) ?? 0;
        if (ctx.HasArg("expected"))
            yield return ctx.Settled(() => Count(), $"{troop.StringId}'s staged points", ctx.ArgInt("expected"), timeout: Sync);
        ctx.Report("value", Count());
    }

    /// <summary>
    /// Server (single player): puts the player's party at a town's gate, so the player can enter it. In Coop use
    /// party.toTownGate, which moves it through Coop.
    /// </summary>
    private static IEnumerable<StepWait?> ToTown(StepContext ctx)
    {
        var party = HeroArg(ctx).PartyBelongedTo ?? throw new StepFailedException("the hero has no party");
        var town = Settlement.Find(ctx.Arg("town")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("town")}'");
        var position = party.GetType().GetProperty("Position");
        position!.SetValue(party, town.GatePosition);
        yield return ctx.Until(() => party.Position.Distance(town.GatePosition) < 1f, $"the party at {town.StringId}'s gate", Sync);
        ctx.Report("value", town.StringId);
    }

    /// <summary>
    /// Player: in a fief's town menu, picks Retinues' "train troops" option, answers its picker with every troop, and
    /// lets time run until the staged points are trained. The party must be in the town (settlement.enter).
    /// </summary>
    private static IEnumerable<StepWait?> TrainInTown(StepContext ctx)
    {
        var troop = TroopArg(ctx);
        var skill = SkillArg(ctx);
        var before = troop.GetSkill(skill);
        var staged = TrainStagingBehavior.Get(troop)?.Sum(p => p.PointsRemaining) ?? 0;
        ctx.Assert(staged > 0, $"{troop.StringId} has nothing staged to train");
        var menu = Campaign.Current.CurrentMenuContext ?? throw new StepFailedException("no game menu is open (enter the town first)");
        var option = menu.GameMenu.MenuOptions.FirstOrDefault(o => o.IdString.IndexOf("train", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new StepFailedException("the town menu has no training option: " + string.Join(", ", menu.GameMenu.MenuOptions.Select(o => o.IdString)));
        ctx.Log($"menu option {option.IdString}");
        foreach (var wait in PickAllFromMulti(ctx, () => option.RunConsequence(menu))) yield return wait;
        Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppableFastForward;
        try
        {
            yield return ctx.Until(() =>
            {
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop)
                    Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppableFastForward;
                return troop.GetSkill(skill) >= before + staged;
            }, $"{troop.StringId}'s {skill.StringId} to be trained to {before + staged} (is {troop.GetSkill(skill)})", TimeSpan.FromMinutes(4));
        }
        finally
        {
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        }
        ctx.Report("value", troop.GetSkill(skill));
    }

    // ---- import and export ---------------------------------------------------------------------------------

    // In the run folder, not Retinues' own Exports folder: a ModderLords launch reaches the module through a junction
    // in its launch view, and a folder made there never appears (Retinues' auto backup fails the same way).
    private static string ExportName(StepContext ctx) => Path.Combine(ctx.RunFolder, ctx.Arg("file", "retinues-export.xml"));

    /// <summary>Player: exports the player's troops to XML, as the export button with "Player Troops" does.</summary>
    private static IEnumerable<StepWait?> ExportTroops(StepContext ctx)
    {
        var path = TroopImportExport.ExportUnified(ExportName(ctx), includeCustom: true, includeCultures: false);
        ctx.Assert(File.Exists(path), $"the export wrote no file at {path}");
        ctx.Report("value", path);
        yield break;
    }

    /// <summary>Player: imports the player's troops from the XML written by retinues.exportTroops.</summary>
    private static IEnumerable<StepWait?> ImportTroops(StepContext ctx)
    {
        ctx.Assert(TroopImportExport.ImportUnified(ExportName(ctx), TroopImportExport.ImportScope.CustomOnly), $"the import of {ExportName(ctx)} failed");
        yield return null;
        ctx.Report("value", ExportName(ctx));
    }

    /// <summary>Player: the MCM's export button, which asks what to export (in Coop it says import and export are off).</summary>
    private static IEnumerable<StepWait?> AskExport(StepContext ctx)
    {
        TroopImportExport.PromptAndExport();
        yield return ctx.Delay(TimeSpan.FromSeconds(1));
        InformationManager.HideInquiry();
        ctx.Report("value", "asked");
    }

    // ---- volunteers ----------------------------------------------------------------------------------------

    /// <summary>
    /// The volunteers on offer in a town (<c>town</c>): how many are the player's own clan or kingdom troops. With
    /// <c>expected</c> "custom", at least one must be.
    /// </summary>
    private static IEnumerable<StepWait?> Volunteers(StepContext ctx)
    {
        var town = Settlement.Find(ctx.Arg("town")) ?? throw new StepFailedException($"no settlement '{ctx.Arg("town")}'");
        IEnumerable<CharacterObject> All() => town.Notables.SelectMany(n => n.VolunteerTypes ?? Array.Empty<CharacterObject>()).Where(c => c != null);
        int Custom() => All().Count(c => new WCharacter(c.StringId).IsCustom);
        if (ctx.Arg("expected", "") == "custom")
            yield return ctx.Until(() => Custom() > 0, $"a custom volunteer in {town.StringId} ({string.Join(",", All().Select(c => c.StringId))})", Sync);
        yield return ctx.Settled(() => string.Join(",", All().Select(c => c.StringId)), $"{town.StringId}'s volunteers");
        ctx.Report("custom", Custom());
        ctx.Report("value", string.Join(",", All().Select(c => c.StringId)));
    }

    // ---- pop-ups -------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="trigger"/> and answers the yes/no pop-up it shows: affirmative or negative.</summary>
    private static IEnumerable<StepWait?> Answer(StepContext ctx, Action trigger, bool affirmative)
    {
        InquiryData? asked = null;
        void Capture(InquiryData data, bool pause, bool priority) => asked = data;
        InformationManager.OnShowInquiry += Capture;
        try
        {
            trigger();
            yield return ctx.Until(() => asked != null, "the pop-up", TimeSpan.FromSeconds(10));
        }
        finally
        {
            InformationManager.OnShowInquiry -= Capture;
        }
        ctx.Log($"pop-up '{asked!.TitleText}': {(affirmative ? asked.AffirmativeText : asked.NegativeText)}");
        foreach (var wait in UntilShown(ctx, asked)) yield return wait;
        InformationManager.HideInquiry();
        (affirmative ? asked.AffirmativeAction : asked.NegativeAction)?.Invoke();
    }

    /// <summary>Runs <paramref name="trigger"/> and answers the picker it shows with the one element <paramref name="pick"/> matches.</summary>
    private static IEnumerable<StepWait?> PickFromMulti(StepContext ctx, Action trigger, Func<InquiryElement, bool> pick, string what)
    {
        MultiSelectionInquiryData? asked = null;
        void Capture(MultiSelectionInquiryData data, bool pause, bool priority) => asked = data;
        MBInformationManager.OnShowMultiSelectionInquiry += Capture;
        try
        {
            trigger();
            yield return ctx.Until(() => asked != null, "the picker", TimeSpan.FromSeconds(10));
        }
        finally
        {
            MBInformationManager.OnShowMultiSelectionInquiry -= Capture;
        }
        var element = asked!.InquiryElements.FirstOrDefault(pick) ?? throw new StepFailedException($"'{asked.TitleText}' offers no {what}");
        ctx.Log($"picker '{asked.TitleText}': {element.Title}");
        InformationManager.HideInquiry();
        asked.AffirmativeAction?.Invoke(new List<InquiryElement> { element });
    }

    /// <summary>Runs <paramref name="trigger"/> and answers the picker it shows with every element it allows.</summary>
    private static IEnumerable<StepWait?> PickAllFromMulti(StepContext ctx, Action trigger)
    {
        MultiSelectionInquiryData? asked = null;
        void Capture(MultiSelectionInquiryData data, bool pause, bool priority) => asked = data;
        MBInformationManager.OnShowMultiSelectionInquiry += Capture;
        try
        {
            trigger();
            yield return ctx.Until(() => asked != null, "the picker", TimeSpan.FromSeconds(10));
        }
        finally
        {
            MBInformationManager.OnShowMultiSelectionInquiry -= Capture;
        }
        var all = asked!.InquiryElements.Where(e => e.IsEnabled).Take(Math.Max(1, asked.MaxSelectableOptionCount)).ToList();
        ctx.Log($"picker '{asked.TitleText}': {string.Join(", ", all.Select(e => e.Title))}");
        InformationManager.HideInquiry();
        asked.AffirmativeAction?.Invoke(all);
    }

    /// <summary>
    /// Waits until a pop-up is the one on screen (the game shows one at a time and queues the rest), so HideInquiry
    /// closes it and not one in front of it. Where the active pop-up cannot be read (no Gauntlet UI), goes on at once.
    /// </summary>
    private static IEnumerable<StepWait?> UntilShown(StepContext ctx, InquiryData data)
    {
        var field = ActiveQuery.Value;
        if (field == null) yield break;
        yield return ctx.Until(() => ReferenceEquals(field.GetValue(null), data), $"the pop-up '{data.TitleText}' to be on screen", TimeSpan.FromSeconds(60));
    }

    private static readonly Lazy<FieldInfo?> ActiveQuery = new Lazy<FieldInfo?>(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name == "TaleWorlds.MountAndBlade.GauntletUI")
        .Select(a => a.GetType("TaleWorlds.MountAndBlade.GauntletUI.GauntletQueryManager", false))
        .FirstOrDefault(t => t != null)
        ?.GetField("_activeQueryData", BindingFlags.Static | BindingFlags.NonPublic));

    private static SkillObject SkillArg(StepContext ctx)
    {
        var id = ctx.Arg("skill", "OneHanded");
        return MBObjectManager.Instance.GetObject<SkillObject>(id) ?? throw new StepFailedException($"no skill '{id}'");
    }
}
