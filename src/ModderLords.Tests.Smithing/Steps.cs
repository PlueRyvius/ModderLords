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
        InGameTests.Register("smithing.designer", StepSide.Client, Designer);
        InGameTests.Register("smithing.smelt", StepSide.Client, Smelt);
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
    /// (the v0.9.49 hand install that left out changed data) fails here with MS2's own reason. So does an adapter that
    /// loaded with a patch it could not hook: MS2 says "UNHOOKED: SpawnAgent" or "FAILED" in its loader status and
    /// carries on, which left spawned soldiers undyed on game v1.5.7 while every check passed.
    /// Reports MS2's settings that differ from its defaults, so a config that changes what the checks see (crafts made
    /// free by material_cost_pct 0) shows in every run.
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

        var broken = new List<string>();
        foreach (var (name, status) in LoaderStatuses())
        {
            ctx.Report("status." + name, status);
            ctx.Log($"{name}: {status}");
            if (status.IndexOf("UNHOOKED", StringComparison.Ordinal) >= 0 || status.IndexOf("FAILED", StringComparison.Ordinal) >= 0)
                broken.Add($"{name}: {status}");
        }
        var changed = NonDefaultSettings();
        ctx.Report("settings", changed.Count == 0 ? "all defaults" : string.Join("; ", changed));
        ctx.Log("settings not at their defaults: " + (changed.Count == 0 ? "none" : string.Join("; ", changed)));

        // value is compared across sides in Coop: the catalog only, not this PC's settings or adapters.
        ctx.Report("value", $"{version} parts={cat.Components.Count} catalog={cat.Hash.Substring(0, 12)}");
        ctx.Assert(broken.Count == 0, "MS2 loaded with a patch it could not apply: " + string.Join(" | ", broken));
        yield break;
    }

    /// <summary>
    /// Every public static string named "...Status" on MS2's SubModule (NativeUiLoaderStatus, and its siblings for
    /// the other adapters), by reflection: which ones exist depends on the MS2 build.
    /// </summary>
    private static IEnumerable<(string Name, string Status)> LoaderStatuses()
    {
        var type = typeof(CraftingDesigner).Assembly.GetType("ModularSmithing.Crafting.SubModule");
        if (type == null) yield break;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (var p in type.GetProperties(flags).Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Status", StringComparison.Ordinal) && p.GetIndexParameters().Length == 0))
            if (p.GetValue(null) is string s && s.Length > 0) yield return (p.Name, s);
        foreach (var f in type.GetFields(flags).Where(f => f.FieldType == typeof(string) && f.Name.EndsWith("Status", StringComparison.Ordinal)))
            if (f.GetValue(null) is string s && s.Length > 0) yield return (f.Name, s);
    }

    /// <summary>MS2's settings whose value differs from a fresh MSmithSettings, as "Field=value (default x)".</summary>
    private static List<string> NonDefaultSettings()
    {
        var changed = new List<string>();
        object? defaults;
        try { defaults = Activator.CreateInstance(typeof(MSmithSettings)); }
        catch (MissingMethodException) { return changed; }
        foreach (var f in typeof(MSmithSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var now = f.GetValue(MSmithConfig.Settings);
            var then = f.GetValue(defaults);
            if (!Equals(now, then)) changed.Add($"{f.Name}={Show(now)} (default {Show(then)})");
        }
        return changed;
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

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

        // The designer's preview, what the player saw before pressing Craft. It shows common quality; the rolled quality
        // scales the weight by its mass factor (an inferior piece is heavier), so the preview's weight times that factor
        // is the item's. Its cost is compared when the craft rolled common.
        double previewKg = MSmithSettings.ScalePct(preview.MassGrams, MSmithConfig.Settings.WeightPct) / 1000.0;
        var tiers = cat.QualityTiers;
        double factor = tiers.TryGetValue(record.TierId ?? "common", out var rolled) && tiers.TryGetValue("common", out var common) && common.MassFactorFP != 0
            ? (double)rolled.MassFactorFP / common.MassFactorFP : 1.0;
        double previewAtQuality = previewKg * factor;
        detail.AppendLine($"preview: {previewKg:0.###} kg at common, {previewAtQuality:0.###} kg at {record.TierId} (mass x{factor:0.###}); cost {string.Join(", ", (preview.MaterialCost ?? new Dictionary<string, int>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (Math.Abs(item.Weight - previewAtQuality) > 0.011 + 0.002 * previewAtQuality)
            problems.Add($"the designer previewed {previewKg:0.###} kg ({previewAtQuality:0.###} kg at {record.TierId}), the item weighs {item.Weight:0.###} kg");
        if (record.TierId == "common" && MSmithConfig.Settings.RequireMaterials && preview.MaterialCost != null)
            foreach (var k in preview.MaterialCost.Keys.Union(taken.Keys))
            {
                int shown = preview.MaterialCost.TryGetValue(k, out var p) ? MSmithSettings.ScalePct(p, MSmithConfig.Settings.MaterialCostPct) : 0;
                taken.TryGetValue(k, out int got);
                if (shown != got) problems.Add($"the designer showed {shown} {k}, the craft took {got}");
            }
        else detail.AppendLine($"preview cost not compared: this craft rolled {record.TierId}");

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

    // ---- the designer -----------------------------------------------------------------------------------------------

    /// <summary>
    /// MS2's designer as the player opens it, built without its screen, then driven through its own tab and button
    /// commands: armor thins down to 50% and bows to 75% (<c>SizeMinPct</c>); gloves and boots have no coverage
    /// choice; body coverage reads "Torso", "Torso and arms", "Torso, arms and legs"; the damage-types row shows for
    /// armor and can be toggled only with the combat patch in.
    /// </summary>
    private static IEnumerable<StepWait?> Designer(StepContext ctx)
    {
        var cat = CraftingDesigner.SharedCatalog;
        var vm = new CraftingDesignerVM(cat, () => { }, (_, _, _, _, _) => { }, RuntimeDonorRegistry.Get(cat));
        var problems = new List<string>();
        var seen = new StringBuilder();
        void Expect<T>(string what, T got, T want)
        {
            seen.AppendLine($"{what}: {got}");
            if (!EqualityComparer<T>.Default.Equals(got, want)) problems.Add($"{what} is {got}, expected {want}");
        }
        try
        {
            vm.ExecuteSelectSlot0();   // head
            Expect("armor SizeMinPct", vm.SizeMinPct, 50);
            Expect("armor ShowDamageTypes", vm.ShowDamageTypes, true);
            var patch = typeof(CraftingDesigner).Assembly.GetType("ModularSmithing.Crafting.DamageTypeProfiles")?.GetField("PatchActive")?.GetValue(null) as bool?;
            Expect("CanToggleDamageTypes (combat patch " + (patch == true ? "in" : "out") + ")", vm.CanToggleDamageTypes, patch == true);
            vm.ExecuteSelectSlot3();   // hands
            Expect("hands HasCoverageControls", vm.HasCoverageControls, false);
            vm.ExecuteSelectSlot2();   // legs
            Expect("legs HasCoverageControls", vm.HasCoverageControls, false);
            vm.ExecuteSelectSlot1();   // body: standard coverage first, then cycled through extended and minimal
            Expect("body HasCoverageControls", vm.HasCoverageControls, true);
            var labels = new List<string>();
            for (int i = 0; i < 3; i++) { labels.Add(vm.CoverageText); vm.ExecuteCycleCoverage(); }
            Expect("body coverage labels", string.Join(" | ", labels), "Torso and arms | Torso, arms and legs | Torso");
            vm.ExecuteCycleFamily();   // weapons: the first tab is the bow
            Expect("bow SizeMinPct", vm.SizeMinPct, 75);
            Expect("bow ShowDamageTypes", vm.ShowDamageTypes, false);
        }
        finally { vm.OnFinalize(); }
        ctx.Dump("designer", seen.ToString());
        ctx.Report("value", problems.Count == 0 ? "as designed" : problems.Count + " difference(s)");
        ctx.Assert(problems.Count == 0, string.Join("; ", problems));
        yield break;
    }

    // ---- smelting ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Smelts one armor piece the way the smithy's smelting tab does (the game's CraftingCampaignBehavior.DoSmelting,
    /// which MS2 patches), and checks the party gained exactly what MS2's cost model says it smelts into
    /// (ArmorSmelting.Return), and lost the piece. <c>item</c>: an item id, or <c>old</c> for the first armor piece
    /// MS2 has a record of that is in the party (in an upgrade world: one made by the earlier release).
    /// </summary>
    private static IEnumerable<StepWait?> Smelt(StepContext ctx)
    {
        var hero = Hero.MainHero;
        var roster = MobileParty.MainParty.ItemRoster;
        ItemObject? item;
        if (ctx.Arg("item") == "old")
        {
            var records = SmithingPersistenceBehavior.Current?.Records;
            ctx.Assert(records != null, "MS2's persistence behaviour is not running");
            item = records!.Select(r => MBObjectManager.Instance.GetObject<ItemObject>(r.StringId))
                .FirstOrDefault(i => i?.ArmorComponent != null && roster.GetItemNumber(i) > 0);
            ctx.Assert(item != null, "no crafted armor piece MS2 has a record of is in the party");
        }
        else item = MBObjectManager.Instance.GetObject<ItemObject>(ctx.Arg("item"));
        ctx.Assert(item != null && roster.GetItemNumber(item) > 0, $"{ctx.Arg("item")} is not in the party");
        ctx.Assert(ArmorSmelting.Enabled, "armor smelting is off in MS2's settings (smelt_armor)");

        var expected = ArmorSmelting.Return(item!);
        ctx.Assert(expected.Count > 0, $"MS2 says {item!.StringId} smelts into nothing");
        var materials = expected.Keys.ToDictionary(k => k, k => NativeSmithingMaterialModel.MaterialItem(k), StringComparer.Ordinal);
        var before = materials.ToDictionary(kv => kv.Key, kv => kv.Value == null ? 0 : roster.GetItemNumber(kv.Value), StringComparer.Ordinal);
        int piecesBefore = roster.GetItemNumber(item);
        TopUpStamina();

        var behavior = Campaign.Current.GetCampaignBehavior<CraftingCampaignBehavior>();
        ctx.Assert(behavior != null, "the game's crafting behaviour is not running");
        var doSmelting = typeof(CraftingCampaignBehavior).GetMethod("DoSmelting", new[] { typeof(Hero), typeof(EquipmentElement) });
        ctx.Assert(doSmelting != null, "CraftingCampaignBehavior.DoSmelting(Hero, EquipmentElement) is not in this game");
        // The party's own entry, quality modifier included, as the smelting list hands it over: a crafted piece is
        // "inferior" or "fine", and the bare item is not in the roster.
        var element = roster.First(e => e.EquipmentElement.Item == item).EquipmentElement;
        doSmelting!.Invoke(behavior, new object[] { hero, element });
        yield return null;

        var problems = new List<string>();
        var detail = new StringBuilder($"smelted {item.StringId} '{item.Name}' (smelt_armor permille {MSmithConfig.Settings.SmeltArmorPermille})\n");
        foreach (var kv in expected)
        {
            int got = materials[kv.Key] == null ? 0 : roster.GetItemNumber(materials[kv.Key]) - before[kv.Key];
            detail.AppendLine($"{kv.Key}: got {got}, MS2 says {kv.Value}");
            if (materials[kv.Key] == null) problems.Add($"the game has no item for MS2's material {kv.Key}");
            else if (got != kv.Value) problems.Add($"got {got} {kv.Key}, MS2's cost model says {kv.Value}");
        }
        if (roster.GetItemNumber(item) != piecesBefore - 1) problems.Add($"the party still has {roster.GetItemNumber(item)} of it (had {piecesBefore})");
        ctx.Dump("smelt", detail.ToString());
        ctx.Report("value", string.Join(", ", expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")));
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
