using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using ModderLords.Testing;
using ModularSmithing.Core;
using ModularSmithing.Crafting;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Tests.Smithing;

/// <summary>
/// Steps for Modular Smithing 2 (test module <c>smithing</c>, docs/INGAME-TEST-HARNESS.md decisions 26–30).
///
/// Crafting goes through MS2's own executor, the one the designer's Craft button reaches, with the materials the craft
/// charges added first. Every check compares the engine's item with what MS2's own calculator says for that recipe and
/// the quality it rolled ("check against the engine"), and suites add property checks on top (a helmet under 4 kg,
/// cloth boots with no ingots).
///
/// What only newer MS2 builds have (damage types, per-slot coverage settings) is reached by reflection, so this DLL also
/// loads into a released MS2 when a suite keeps an upgrade world on it.
/// </summary>
public static class Steps
{
    private const string ModId = "ModularSmithing2";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly List<InquiryData> Inquiries = new();
    private static bool _watching;

    public static void Register()
    {
        InGameTests.Register("ModularSmithing2.health", StepSide.Any, Health);
        InGameTests.Register("smithing.prepare", StepSide.Client, Prepare);
        InGameTests.Register("smithing.craft", StepSide.Client, Craft);
        InGameTests.Register("smithing.itemSame", StepSide.Any, ItemSame);
        InGameTests.Register("smithing.records", StepSide.Any, Records);
        InGameTests.Register("smithing.oldItemsKeepStats", StepSide.Any, OldItemsKeepStats);
        InGameTests.Register("smithing.coverageChoice", StepSide.Any, CoverageChoice);
        InGameTests.Register("smithing.noHelperDonors", StepSide.Any, NoHelperDonors);
        InGameTests.Register("smithing.setSetting", StepSide.Any, SetSetting);
        InGameTests.Register("smithing.damageProfile", StepSide.Any, DamageProfile);
        InGameTests.Register("smithing.configSnapshot", StepSide.Any, ConfigSnapshot);
        InGameTests.Register("smithing.configRestore", StepSide.Any, ConfigRestore);
        InGameTests.Register("smithing.watchNotice", StepSide.Client, WatchNotice);
        InGameTests.Register("smithing.answerNotice", StepSide.Client, AnswerNotice);
        QuietNotice();
    }

    /// <summary>
    /// Marks MS2's one-time damage types notice as already shown, in memory only. It would otherwise open on the first
    /// map tick of every world the run makes or loads and wait for an answer nobody gives. The notice suite turns it
    /// back on for its own check. MS2 builds without the notice have no such setting, and this does nothing.
    /// </summary>
    private static void QuietNotice() =>
        typeof(MSmithSettings).GetField("DamageTypesNoticeShown")?.SetValue(MSmithConfig.Settings, true);

    // ---- health ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// MS2's load status (decision 26): its version, its part catalog's size and hash. A catalog that fails to load
    /// (the v0.9.49 hand install that left out changed data) fails here with MS2's own reason.
    /// </summary>
    private static IEnumerable<StepWait?> Health(StepContext ctx)
    {
        Catalog cat;
        try { cat = CraftingDesigner.SharedCatalog; }
        catch (Exception ex) { throw new StepFailedException("MS2's part catalog did not load: " + ex.GetBaseException().Message); }
        ctx.Assert(cat != null && cat.Components.Count > 0, "MS2's part catalog is empty");
        var version = ModuleHelper.GetModules().FirstOrDefault(m => m.Id == ModId)?.Version.ToString() ?? "?";
        ctx.Report("version", version);
        ctx.Report("parts", cat!.Components.Count);
        ctx.Report("catalogHash", cat.Hash);
        ctx.Report("value", $"{version} parts={cat.Components.Count} catalog={cat.Hash.Substring(0, 12)}");
        yield break;
    }

    // ---- crafting -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The player's hero gets Smithing <c>skill</c> (default 330: every part unlocked) and full stamina. Crafts charge
    /// materials at 100% at least, in memory only: a player's config may make them free (require_materials off, or
    /// material_cost_pct 0), and the checks on what a craft charges would then have nothing to read.
    /// </summary>
    private static IEnumerable<StepWait?> Prepare(StepContext ctx)
    {
        ctx.Assert(Hero.MainHero != null, "there is no player hero");
        var skill = ctx.ArgInt("skill", 330);
        Hero.MainHero!.SetSkillValue(DefaultSkills.Crafting, skill);
        TopUpStamina();
        var settings = MSmithConfig.Settings;
        if (!settings.RequireMaterials || settings.MaterialCostPct <= 0)
        {
            ctx.Log($"this config makes crafts free (require_materials {settings.RequireMaterials}, material_cost_pct {settings.MaterialCostPct}); charging 100% for the run, in memory only");
            settings.RequireMaterials = true;
            settings.MaterialCostPct = 100;
        }
        ctx.Report("value", CraftDiagnostics.SafeCraftingSkill(Hero.MainHero));
        yield break;
    }

    private static void TopUpStamina()
    {
        var b = Campaign.Current.GetCampaignBehavior<ICraftingCampaignBehavior>();
        if (b != null) b.SetHeroCraftingStamina(Hero.MainHero, b.GetMaxHeroCraftingStamina(Hero.MainHero));
    }

    /// <summary>
    /// Crafts one recipe as the designer's Craft button does (MS2's crafting executor), after adding the materials it
    /// charges. Checks the item against MS2's calculator for the quality it rolled: protection (or the weapon's numbers),
    /// weight, the materials taken from the party exactly, the item in the inventory, and for armor the tier the designer
    /// predicted against the engine's <c>ItemObject.Tier</c>.
    /// Optional property checks: <c>maxKg</c>, <c>minKg</c>, <c>noIngots</c>, <c>ingotGrades</c>, <c>missileSpeed</c>.
    /// Reports <c>value</c> (the item id), <c>weight</c>, <c>armor</c>, <c>tier</c>, <c>cost</c>.
    /// </summary>
    private static IEnumerable<StepWait?> Craft(StepContext ctx)
    {
        var cat = CraftingDesigner.SharedCatalog;
        string family = ctx.Arg("family", "armor");
        string slot = ctx.Arg("slot");
        var recipe = new Recipe
        {
            Family = family,
            Slot = slot,
            Inner = ctx.Arg("inner", Recipe.None),
            Middle = ctx.Arg("middle", Recipe.None),
            Outer = ctx.Arg("outer", Recipe.None),
            Extra = ctx.Arg("extra", Recipe.None),
            SizePct = ctx.ArgInt("size", 100),
            AmmoKind = family == "ammo" ? slot : "none",
            Coverage = ctx.HasArg("coverage") ? ctx.Arg("coverage") : null,
        };
        int batch = ctx.ArgInt("batch", 0);
        var hero = Hero.MainHero;
        int skill = CraftDiagnostics.SafeCraftingSkill(hero);
        TopUpStamina();

        // Enough of every material the dearest quality could charge, so the craft never fails for want of them.
        var roster = MobileParty.MainParty.ItemRoster;
        var worst = CraftCalculator.Compute(cat, recipe, skill, cat.QualityTiers.Values.OrderByDescending(q => q.MaterialFactorFP).First(), null, batch).MaterialCost;
        foreach (var kv in worst)
        {
            var mat = NativeSmithingMaterialModel.MaterialItem(kv.Key);
            ctx.Assert(mat != null, $"the game has no item for MS2's material {kv.Key}");
            roster.AddToCounts(mat, kv.Value * 2 + 5);
        }
        var before = worst.Keys.ToDictionary(k => k, k => roster.GetItemNumber(NativeSmithingMaterialModel.MaterialItem(k)), StringComparer.Ordinal);

        var preview = CraftPreview.Compute(cat, recipe, skill, batch);
        var outcome = CraftingExecutors.Current.Execute(cat, recipe, "{=}ML test " + slot, hero, skill,
            default(DonorVisual), new NativeSmithingStaminaModel(), batch);
        ctx.Assert(outcome != null && outcome.Success, "MS2 did not craft it: " + (outcome?.Message ?? "no outcome"));
        var item = outcome!.Item;
        ctx.Assert(item != null, "the craft reported success but made no item");
        yield return null;
        ctx.Assert(roster.GetItemNumber(item) > 0, $"{item!.StringId} is not in the party's inventory");

        var record = SmithingPersistenceBehavior.Current?.Records.FirstOrDefault(r => r.StringId == item.StringId);
        ctx.Assert(record != null, $"MS2 keeps no record for {item.StringId}, so it would not survive a save");
        var expected = SurvivalCheck.Expected(cat, record!.RecipeCanonical, record.SmithingEffect, record.TierId, batch);
        var problems = new List<string>();
        var detail = new StringBuilder();
        detail.AppendLine($"recipe {recipe.Canonical()}");
        detail.AppendLine($"item {item.StringId} '{item.Name}' quality {record.TierId}, skill {record.SmithingEffect}");

        // Weight: the calculator's grams, scaled by MS2's weight_pct as the item XML is.
        double expectedKg = MSmithSettings.ScalePct(expected.MassGrams, MSmithConfig.Settings.WeightPct) / 1000.0;
        if (Math.Abs(item.Weight - expectedKg) > 0.011) problems.Add($"weighs {item.Weight:0.###} kg, MS2's calculator says {expectedKg:0.###} kg");
        detail.AppendLine($"weight {item.Weight:0.###} kg (calculator {expectedKg:0.###})");

        string armorText = "";
        if (family == "armor")
        {
            var ac = item.ArmorComponent;
            ctx.Assert(ac != null, $"{item.StringId} has no armor component");
            var live = new Dictionary<string, int>
            {
                ["head"] = ac!.HeadArmor, ["body"] = ac.BodyArmor, ["arm"] = ac.ArmArmor, ["leg"] = ac.LegArmor,
            };
            foreach (var kv in live)
            {
                int want = expected.Armor != null && expected.Armor.TryGetValue(kv.Key, out var w) ? w : 0;
                if (kv.Value != want) problems.Add($"{kv.Key} armor {kv.Value}, calculator {want}");
            }
            armorText = $"{live["head"]}/{live["body"]}/{live["arm"]}/{live["leg"]}";
            detail.AppendLine($"armor head/body/arm/leg {armorText}");

            if (ctx.Arg("checkTier", "true") == "true")
            {
                long mass = MSmithSettings.ScalePct(preview.MassGrams, MSmithConfig.Settings.WeightPct);
                int predicted = NativeItemTier.Tier(NativeItemTier.Tierf(family, slot, preview.Stats, mass));
                // ItemObject.Tier is ItemTiers, where Tier1 is 0: one below the tier the game and the designer show.
                int engine = (int)item.Tier + 1;
                detail.AppendLine($"tier: designer {predicted}, engine {engine}");
                ctx.Report("tier", engine);
                ctx.Report("predictedTier", predicted);
                if (predicted != engine) problems.Add($"the designer predicted tier {predicted}, the engine gave {engine}");
            }
        }
        if (ctx.HasArg("missileSpeed"))
        {
            var weapon = item.WeaponComponent?.PrimaryWeapon;
            ctx.Assert(weapon != null, $"{item.StringId} has no weapon component");
            if (weapon!.MissileSpeed != ctx.ArgInt("missileSpeed")) problems.Add($"missile speed {weapon.MissileSpeed}, expected {ctx.ArgInt("missileSpeed")}");
            detail.AppendLine($"missile speed {weapon.MissileSpeed}, stack {weapon.MaxDataValue}");
        }

        // Materials: what left the party is exactly what MS2 charges for this recipe at the rolled quality.
        var charged = expected.MaterialCost.ToDictionary(kv => kv.Key, kv => MSmithSettings.ScalePct(kv.Value, MSmithConfig.Settings.MaterialCostPct), StringComparer.Ordinal);
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in before.Keys)
        {
            int used = before[k] - roster.GetItemNumber(NativeSmithingMaterialModel.MaterialItem(k));
            if (used != 0) taken[k] = used;
        }
        if (MSmithConfig.Settings.RequireMaterials)
            foreach (var k in charged.Keys.Union(taken.Keys))
            {
                charged.TryGetValue(k, out int want);
                taken.TryGetValue(k, out int got);
                if (want != got) problems.Add($"took {got} {k}, MS2 charges {want}");
            }
        string costText = string.Join(", ", taken.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
        detail.AppendLine("materials taken: " + costText);

        // Property checks a suite asks for.
        if (ctx.HasArg("maxKg") && item.Weight > double.Parse(ctx.Arg("maxKg"), Inv)) problems.Add($"weighs {item.Weight:0.###} kg, more than {ctx.Arg("maxKg")}");
        if (ctx.HasArg("minKg") && item.Weight < double.Parse(ctx.Arg("minKg"), Inv)) problems.Add($"weighs {item.Weight:0.###} kg, less than {ctx.Arg("minKg")}");
        var grades = taken.Keys.Where(k => k.StartsWith("ironIngot", StringComparison.Ordinal)).ToList();
        if (ctx.Arg("noIngots", "false") == "true" && (grades.Count > 0 || taken.ContainsKey("charcoal")))
            problems.Add("charged ingots or charcoal for a piece with no metal layer: " + costText);
        if (ctx.HasArg("ingotGrades") && grades.Count != ctx.ArgInt("ingotGrades"))
            problems.Add($"charged {grades.Count} ingot grade(s) ({string.Join(", ", grades)}), expected {ctx.ArgInt("ingotGrades")}");

        ctx.Dump("craft", detail.ToString());
        ctx.Report("weight", item.Weight.ToString("0.###", Inv));
        ctx.Report("armor", armorText);
        ctx.Report("cost", costText);
        ctx.Report("value", item.StringId);
        ctx.Assert(problems.Count == 0, $"{item.StringId}: " + string.Join("; ", problems));
    }

    /// <summary>After a reload: the item <c>item</c> still exists with the <c>weight</c> and <c>armor</c> reported before.</summary>
    private static IEnumerable<StepWait?> ItemSame(StepContext ctx)
    {
        var id = ctx.Arg("item");
        var item = MBObjectManager.Instance.GetObject<ItemObject>(id);
        ctx.Assert(item != null && item.IsReady, $"{id} did not come back after the reload");
        var weight = item!.Weight.ToString("0.###", Inv);
        var ac = item.ArmorComponent;
        var armor = ac == null ? "" : $"{ac.HeadArmor}/{ac.BodyArmor}/{ac.ArmArmor}/{ac.LegArmor}";
        ctx.Assert(weight == ctx.Arg("weight") && armor == ctx.Arg("armor", ""),
            $"{id} came back as {weight} kg, armor {armor}; before the save it was {ctx.Arg("weight")} kg, armor {ctx.Arg("armor", "")}");
        ctx.Report("value", weight + " " + armor);
        yield break;
    }

    // ---- saves ----------------------------------------------------------------------------------------------------

    /// <summary>Every item MS2 keeps a record of exists in the game (no record lost its item on load).</summary>
    private static IEnumerable<StepWait?> Records(StepContext ctx)
    {
        var records = SmithingPersistenceBehavior.Current?.Records;
        ctx.Assert(records != null, "MS2's persistence behaviour is not running");
        var missing = records!.Where(r => MBObjectManager.Instance.GetObject<ItemObject>(r.StringId) is not { IsReady: true }).Select(r => r.StringId).ToList();
        ctx.Log($"{records.Count} crafted item record(s)");
        ctx.Assert(records.Count >= ctx.ArgInt("atLeast", 0), $"{records.Count} crafted item record(s), expected at least {ctx.ArgInt("atLeast", 0)}");
        ctx.Assert(missing.Count == 0, $"{missing.Count} record(s) have no item after loading: {string.Join(", ", missing.Take(10))}");
        ctx.Report("value", records.Count);
        yield break;
    }

    /// <summary>
    /// On a save made with an older MS2: every crafted armor piece keeps the weight and protection it was made with
    /// (MS2 never rebuilds old items to new formulas). Reports how many differ from today's formula, for the log.
    /// </summary>
    private static IEnumerable<StepWait?> OldItemsKeepStats(StepContext ctx)
    {
        var records = SmithingPersistenceBehavior.Current?.Records;
        ctx.Assert(records != null, "MS2's persistence behaviour is not running");
        var cat = CraftingDesigner.SharedCatalog;
        int checkedCount = 0, stale = 0;
        var problems = new List<string>();
        foreach (var r in records!)
        {
            var item = MBObjectManager.Instance.GetObject<ItemObject>(r.StringId);
            if (item?.ArmorComponent == null || string.IsNullOrEmpty(r.ItemXml)) continue;
            var doc = new XmlDocument();
            doc.LoadXml(r.ItemXml);
            var root = doc.DocumentElement!;
            checkedCount++;
            if (float.TryParse(root.GetAttribute("weight"), NumberStyles.Float, Inv, out var savedKg) && Math.Abs(savedKg - item.Weight) > 0.001)
                problems.Add($"{r.StringId} weighs {item.Weight:0.###} kg, saved as {savedKg:0.###}");
            var armorEl = root.SelectSingleNode(".//Armor") as XmlElement;
            if (armorEl != null)
                foreach (var (attr, live) in new[] { ("head_armor", item.ArmorComponent.HeadArmor), ("body_armor", item.ArmorComponent.BodyArmor), ("arm_armor", item.ArmorComponent.ArmArmor), ("leg_armor", item.ArmorComponent.LegArmor) })
                    if (int.TryParse(armorEl.GetAttribute(attr), NumberStyles.Integer, Inv, out var saved) && saved != live)
                        problems.Add($"{r.StringId} {attr} {live}, saved as {saved}");
            try
            {
                var now = SurvivalCheck.Expected(cat, r.RecipeCanonical, r.SmithingEffect, r.TierId ?? "common");
                double nowKg = MSmithSettings.ScalePct(now.MassGrams, MSmithConfig.Settings.WeightPct) / 1000.0;
                if (Math.Abs(nowKg - item.Weight) > 0.011) stale++;
            }
            catch (Exception ex) { ctx.Log($"{r.StringId}: today's formula cannot compute its recipe: {ex.GetBaseException().Message}"); }
        }
        ctx.Log($"{checkedCount} old armor piece(s) checked; {stale} predate today's formulas (kept as made, by design)");
        ctx.Assert(checkedCount >= ctx.ArgInt("atLeast", 0), $"{checkedCount} crafted armor piece(s) in this save, expected at least {ctx.ArgInt("atLeast", 0)}");
        ctx.Assert(problems.Count == 0, string.Join("; ", problems.Take(10)));
        ctx.Report("stale", stale);
        ctx.Report("value", checkedCount);
        yield break;
    }

    // ---- data properties ------------------------------------------------------------------------------------------

    /// <summary>Whether the designer offers a coverage choice for <c>slot</c>; fails unless it equals <c>expected</c>.</summary>
    private static IEnumerable<StepWait?> CoverageChoice(StepContext ctx)
    {
        var cat = CraftingDesigner.SharedCatalog;
        var slotFor = cat.GetType().GetMethod("CoverageSlotFor");
        ctx.Assert(slotFor != null, "this MS2 has no per-slot coverage settings (Catalog.CoverageSlotFor)");
        var info = slotFor!.Invoke(cat, new object[] { ctx.Arg("slot") });
        var choice = (bool)info!.GetType().GetField("CoverageChoice")!.GetValue(info)!;
        ctx.Report("value", choice);
        ctx.Assert(choice.ToString().Equals(ctx.Arg("expected"), StringComparison.OrdinalIgnoreCase),
            $"{ctx.Arg("slot")}: coverage choice {choice}, expected {ctx.Arg("expected")}");
        yield break;
    }

    /// <summary>
    /// No armor the designer can pick a look from is a weightless, armorless helper piece (Dismemberment (Plus)'s
    /// left/right halves made crafted boots render as one boot).
    /// </summary>
    private static IEnumerable<StepWait?> NoHelperDonors(StepContext ctx)
    {
        var registry = RuntimeDonorRegistry.Get(CraftingDesigner.SharedCatalog);
        var helpers = new List<string>();
        int count = 0;
        foreach (var e in registry.Entries)
        {
            var item = MBObjectManager.Instance.GetObject<ItemObject>(e.ItemId);
            var ac = item?.ArmorComponent;
            if (ac == null) continue;
            count++;
            if (item!.Weight <= 0f && ac.HeadArmor <= 0 && ac.BodyArmor <= 0 && ac.ArmArmor <= 0 && ac.LegArmor <= 0)
                helpers.Add($"{e.ItemId} ({e.Slot})");
        }
        ctx.Log($"{count} armor look(s) checked");
        ctx.Assert(helpers.Count == 0, $"{helpers.Count} helper piece(s) can be a crafted look: {string.Join(", ", helpers.Take(10))}");
        ctx.Report("value", count);
        yield break;
    }

    // ---- settings and damage types -------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> SettingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["damage_types"] = "DamageTypes",
        ["damage_types_notice_shown"] = "DamageTypesNoticeShown",
    };

    private static FieldInfo SettingField(StepContext ctx, string name)
    {
        ctx.Assert(SettingFields.TryGetValue(name, out var field), $"unknown setting {name} (this step knows {string.Join(", ", SettingFields.Keys)})");
        var f = typeof(MSmithSettings).GetField(field!);
        ctx.Assert(f != null, $"this MS2 has no setting {name}");
        return f!;
    }

    /// <summary>Sets an MS2 setting in memory only (the config file is untouched); reports <c>before</c> to put it back.</summary>
    private static IEnumerable<StepWait?> SetSetting(StepContext ctx)
    {
        var f = SettingField(ctx, ctx.Arg("name"));
        var before = f.GetValue(MSmithConfig.Settings);
        f.SetValue(MSmithConfig.Settings, ctx.Arg("value").Equals("true", StringComparison.OrdinalIgnoreCase));
        ctx.Report("before", before);
        ctx.Report("value", f.GetValue(MSmithConfig.Settings));
        yield break;
    }

    /// <summary>
    /// The damage-type multiplier the combat patch would apply for item <c>item</c> in <c>zone</c> against <c>kind</c>
    /// (cut, pierce, blunt), permille. <c>expect</c>: plain (1000), below or above. Needs the combat patch installed
    /// (MS2's NativeUI adapter, with Harmony).
    /// </summary>
    private static IEnumerable<StepWait?> DamageProfile(StepContext ctx)
    {
        var profiles = typeof(CraftingDesigner).Assembly.GetType("ModularSmithing.Crafting.DamageTypeProfiles");
        var kindType = typeof(Catalog).Assembly.GetType("ModularSmithing.Core.DamageKind");
        ctx.Assert(profiles != null && kindType != null, "this MS2 has no damage types");
        var active = (bool)profiles!.GetField("PatchActive")!.GetValue(null)!;
        ctx.Assert(active, "the damage-type combat patch is not installed (MS2's NativeUI adapter needs UIExtenderEx and Harmony)");
        var item = MBObjectManager.Instance.GetObject<ItemObject>(ctx.Arg("item"));
        ctx.Assert(item != null, $"there is no item {ctx.Arg("item")}");
        var kind = Enum.Parse(kindType!, ctx.Arg("kind"), ignoreCase: true);
        int pm = (int)profiles.GetMethod("MultiplierPm")!.Invoke(null, new object[] { item!, ctx.Arg("zone"), kind })!;
        var enabled = (bool)profiles.GetProperty("Enabled")!.GetValue(null)!;
        ctx.Report("value", pm);
        ctx.Report("enabled", enabled);
        var expect = ctx.Arg("expect");
        bool ok = expect switch { "plain" => pm == 1000, "below" => pm < 1000, "above" => pm > 1000, _ => throw new StepFailedException($"expect is plain, below or above, not {expect}") };
        ctx.Assert(ok, $"{ctx.Arg("item")} {ctx.Arg("zone")} against {ctx.Arg("kind")}: {pm} permille, expected {expect}");
        yield break;
    }

    // ---- the damage types notice -----------------------------------------------------------------------------------

    /// <summary>Keeps MS2's config file as it is now, for <c>smithing.configRestore</c>: answering the notice saves it.</summary>
    private static IEnumerable<StepWait?> ConfigSnapshot(StepContext ctx)
    {
        var path = MSmithConfig.FilePath;
        ctx.State["smithing.config"] = File.Exists(path) ? File.ReadAllText(path) : null;
        ctx.Report("value", path);
        yield break;
    }

    /// <summary>Puts MS2's config file back as <c>smithing.configSnapshot</c> found it, and reloads it.</summary>
    private static IEnumerable<StepWait?> ConfigRestore(StepContext ctx)
    {
        ctx.Assert(ctx.State.ContainsKey("smithing.config"), "no smithing.configSnapshot ran before this");
        var path = MSmithConfig.FilePath;
        if (ctx.State["smithing.config"] is string text) File.WriteAllText(path, text);
        else if (File.Exists(path)) File.Delete(path);
        ctx.Report("value", MSmithConfig.Reload());
        // The reload brought the file's "not shown yet" back; the rest of the run must not see the notice.
        QuietNotice();
        yield break;
    }

    /// <summary>From now on, keeps every pop-up the game shows, for <c>smithing.answerNotice</c>.</summary>
    private static IEnumerable<StepWait?> WatchNotice(StepContext ctx)
    {
        lock (Inquiries) Inquiries.Clear();
        if (!_watching)
        {
            InformationManager.OnShowInquiry += (data, _, _) => { lock (Inquiries) Inquiries.Add(data); };
            _watching = true;
        }
        yield break;
    }

    /// <summary>
    /// Waits for MS2's one-time damage types notice and answers it as the player would (<c>answer</c>: on or off), then
    /// checks the settings it set. Campaign time runs while waiting: the notice comes on a map tick.
    /// </summary>
    private static IEnumerable<StepWait?> AnswerNotice(StepContext ctx)
    {
        InquiryData? Notice() { lock (Inquiries) return Inquiries.FirstOrDefault(i => i.TitleText?.IndexOf("damage types", StringComparison.OrdinalIgnoreCase) >= 0); }
        Campaign.Current.TimeControlMode = CampaignTimeControlMode.StoppablePlay;
        yield return ctx.Until(() => Notice() != null, "MS2's damage types notice", TimeSpan.FromSeconds(60));
        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        var notice = Notice()!;
        bool on = ctx.Arg("answer", "off") == "on";
        (on ? notice.AffirmativeAction : notice.NegativeAction)?.Invoke();
        InformationManager.HideInquiry();
        var shown = (bool)SettingField(ctx, "damage_types_notice_shown").GetValue(MSmithConfig.Settings)!;
        var types = (bool)SettingField(ctx, "damage_types").GetValue(MSmithConfig.Settings)!;
        ctx.Report("title", notice.TitleText);
        ctx.Report("value", types);
        ctx.Assert(shown, "answering the notice did not record it as shown, so it would come back");
        ctx.Assert(types == on, $"after answering \"{(on ? "Turn on" : "Keep off")}\" damage types are {(types ? "on" : "off")}");
    }
}
