using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ModderLords.Testing;
using ModularSmithing.Core;
using ModularSmithing.Crafting;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace ModderLords.Tests.Smithing;

/// <summary>
/// Every armor look MS2's designer offers, crafted and dyed, worn by the hero in a town and photographed: the check a
/// game update needs, because MS2 rebuilds its dye textures from the game's own files (MS2's
/// docs/texture-reconstruction-validation.md) and a changed game texture or mesh breaks a look without any error in
/// the code. Checks what a program can tell (the craft, the dye texture rebuild) and photographs what only a person can
/// (colors on the right parts), into a contact sheet.
/// </summary>
internal static class LookSteps
{
    /// <summary>One color per dye region, in region order: far apart, so a region that spills onto another shows.</summary>
    private static readonly string[] RegionColors =
    {
        "#E01010", "#10B010", "#1040F0", "#F0D000", "#E010E0", "#10D0D0",
        "#F0F0F0", "#202020", "#F07800", "#7820F0", "#90F090", "#784010",
    };

    private static readonly Dictionary<string, EquipmentIndex> SlotIndex = new(StringComparer.Ordinal)
    {
        ["head"] = EquipmentIndex.Head, ["body"] = EquipmentIndex.Body, ["legs"] = EquipmentIndex.Leg,
        ["hands"] = EquipmentIndex.Gloves, ["shoulders"] = EquipmentIndex.Cape,
    };

    // Where the camera looks for each slot: height above the feet, distance, and how far above that it sits.
    private static readonly Dictionary<string, (float Height, float Distance, float Rise)> SlotShot = new(StringComparer.Ordinal)
    {
        ["head"] = (1.62f, 1.3f, 0.15f), ["body"] = (1.25f, 2.3f, 0.25f), ["legs"] = (0.4f, 1.6f, 0.2f),
        ["hands"] = (1.0f, 1.8f, 0.2f), ["shoulders"] = (1.35f, 2.3f, 0.25f),
    };

    /// <summary>The item view's vertical field of view (radians): narrow, so the piece isn't distorted.</summary>
    private const float ItemFov = 0.6f;

    /// <summary>How high above the hero the item view hangs the piece (m): past the camera's 2 km far plane from the
    /// ground, looking down a little.</summary>
    private const float ItemAltitude = 1500f;

    internal static void Register() => InGameTests.Register("smithing.everyLook", StepSide.Client, EveryLook);

    private sealed class Row
    {
        public string Id = "", Name = "", Slot = "", Profile = "", Status = "", Message = "", Crafted = "";
        public List<(string Name, string Color)> Regions = new();
        public List<string> Shots = new();
        public bool Failed;
    }

    /// <summary>
    /// Single player, in a town (run settlement.enter first). Picks armor looks from MS2's donor registry: by
    /// <c>ids</c>/<c>idsFile</c> (item or dye profile ids: after a game update or a re-bake, the profiles that changed),
    /// or the first <c>perSlot</c> of each slot (a sample), filtered by <c>slots</c> (a comma list); <c>start</c> and
    /// <c>limit</c> slice the result. Meant for tens of looks a person will actually look at, not all ~700.
    /// For each look: crafts one piece with that look and a
    /// test palette (one bold color per dye region) through MS2's executor, waits for its dye textures (up to
    /// <c>readySeconds</c>, default 60), then photographs it. <c>view</c> "item" (the default) shows the piece on its
    /// own, as MS2's designer does, high over the town against the sky, from the front, left, back and right, lit by a
    /// light at the camera (<c>light</c>, its intensity, default 800) and six fill lights, so no side is in shadow; horse armor too. <c>view</c> "worn" puts it on the
    /// hero in the town center with no other armor, from the front and the back (horse armor is then not photographed).
    ///
    /// Writes evidence\every-look\index.html (a contact sheet: failures first, each look with its region legend) and
    /// results.json. Fails, after the whole slice, when a craft or a dye texture rebuild failed, naming them. Needs the
    /// dye renderer (Harmony and UIExtenderEx loaded) for the colors to show; MS2's own requirement switches are turned
    /// off for the run (in memory) so no look waits on materials, stamina or unlocks.
    /// </summary>
    private static IEnumerable<StepWait?> EveryLook(StepContext ctx)
    {
        ctx.Assert(Settlement.CurrentSettlement?.IsTown == true, "the party is not in a town (run settlement.enter first)");
        ctx.Assert(ArmorDyeItems.RendererStatus.StartsWith("per-item region renderer ready", StringComparison.Ordinal),
            $"MS2's dye renderer is not running ({ArmorDyeItems.RendererStatus}); the colors need Harmony and UIExtenderEx");

        var folder = Path.Combine(string.IsNullOrEmpty(ctx.RunFolder) ? Path.GetTempPath() : ctx.RunFolder, "evidence", "every-look");
        Directory.CreateDirectory(Path.Combine(folder, "shots"));
        var trace = Path.Combine(folder, "trace.txt");
        void Trace(string line)
        {
            ctx.Log(line);
            try { File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); } catch (IOException) { }
        }

        var cat = CraftingDesigner.SharedCatalog;
        var slots = new HashSet<string>(ctx.Arg("slots", "head,body,legs,hands,shoulders,horse_armor").Split(',').Select(s => s.Trim()), StringComparer.Ordinal);
        var wanted = WantedIds(ctx);
        var looks = RuntimeDonorRegistry.Get(cat).Entries
            .Where(e => slots.Contains(e.Slot))
            .GroupBy(e => e.ItemId, StringComparer.Ordinal).Select(g => g.First())
            .Where(e => wanted == null || wanted.Contains(e.ItemId) || (ArmorDyeCatalog.ForDonor(e.ItemId)?.Id is { } p && wanted.Contains(p)))
            .OrderBy(e => e.Slot, StringComparer.Ordinal).ThenBy(e => e.ItemId, StringComparer.Ordinal)
            .ToList();
        if (ctx.ArgInt("perSlot", 0) > 0)
            looks = looks.GroupBy(e => e.Slot).SelectMany(g => g.Take(ctx.ArgInt("perSlot", 0))).ToList();
        looks = looks.Skip(ctx.ArgInt("start", 0)).ToList();
        if (ctx.ArgInt("limit", 0) > 0) looks = looks.Take(ctx.ArgInt("limit", 0)).ToList();
        ctx.Assert(looks.Count > 0, "no armor look matches (slots " + string.Join(",", slots) + (wanted == null ? "" : $", {wanted.Count} listed id(s)") + ")");
        if (wanted != null)
        {
            // A listed id that matched nothing is a typo or a look MS2 no longer offers: say so, don't skip it silently.
            var matched = new HashSet<string>(looks.SelectMany(l => new[] { l.ItemId, ArmorDyeCatalog.ForDonor(l.ItemId)?.Id ?? "" }), StringComparer.Ordinal);
            var unmatched = wanted.Where(id => !matched.Contains(id)).ToList();
            if (unmatched.Count > 0) Trace($"{unmatched.Count} listed id(s) match no look: {string.Join(", ", unmatched.Take(20))}");
            ctx.Report("unmatched", string.Join(",", unmatched));
        }
        Trace($"{looks.Count} look(s) in slots {string.Join(",", slots)}" + (wanted == null ? "" : $", from {wanted.Count} listed id(s)"));

        // "item" (the default): each piece on its own, as the designer shows it, from four sides. "worn": on the hero.
        var view = ctx.Arg("view", "item");
        ctx.Assert(view is "item" or "worn", $"view is \"item\" or \"worn\", not \"{view}\"");
        bool itemView = view == "item";
        float lightIntensity = ctx.ArgInt("light", 800);
        string Shot(string name)
        {
            var bmp = Path.Combine(folder, "shots", name + ".bmp");
            TaleWorlds.Engine.Utilities.TakeScreenshot(bmp);
            return bmp;
        }

        var settings = MSmithConfig.Settings;
        var saved = (settings.RequireMaterials, settings.RequireStamina, settings.RequireUnlocks);
        settings.RequireMaterials = false; settings.RequireStamina = false; settings.RequireUnlocks = false;
        var rows = new List<Row>();
        try
        {
            // The town center, as the town menu's "Take a walk around the town center" opens it.
            int option = MenuOption("town_streets");
            ctx.Assert(option >= 0, $"the current menu ({Campaign.Current.CurrentMenuContext?.GameMenu?.StringId ?? "none"}) has no town_streets option");
            Campaign.Current.GameMenuManager.RunConsequencesOfMenuOption(Campaign.Current.CurrentMenuContext!, option);
            yield return ctx.Until(() => Mission.Current?.MainAgent != null && Mission.Current.MainAgent.IsActive(), "the town center with the hero in it", TimeSpan.FromSeconds(120));
            var agent = Mission.Current!.MainAgent!;
            agent.SetMortalityState(Agent.MortalityState.Invulnerable);
            var bare = new Equipment(agent.SpawnEquipment);
            foreach (var i in SlotIndex.Values) bare[i] = EquipmentElement.Invalid;
            Trace($"town center {Mission.Current.SceneName}");
            // The first screenshot after the scene opens can still be the loading screen, whatever the loading window
            // reports: take one and throw it away.
            var warm = DateTime.UtcNow.AddSeconds(2);
            yield return ctx.Until(() => DateTime.UtcNow >= warm && !TaleWorlds.Engine.LoadingWindow.IsLoadingWindowActive, "the town center to finish loading", TimeSpan.FromSeconds(60));
            var warmShot = Shot("warm-up");
            yield return ctx.Until(() => File.Exists(warmShot) && new FileInfo(warmShot).Length > 0, "the warm-up screenshot", TimeSpan.FromSeconds(10));
            try { File.Delete(warmShot); } catch (IOException) { }

            var hero = Hero.MainHero!;
            int skill = CraftDiagnostics.SafeCraftingSkill(hero);
            int n = 0;
            foreach (var look in looks)
            {
                n++;
                var row = new Row { Id = look.ItemId, Slot = look.Slot };
                rows.Add(row);
                var donor = MBObjectManager.Instance.GetObject<ItemObject>(look.ItemId);
                if (donor == null) { row.Status = "no item"; row.Message = "the game has no item " + look.ItemId; row.Failed = true; continue; }
                row.Name = donor.Name?.ToString() ?? look.ItemId;

                // The test palette: one bold color per region the look shows.
                string? palette = null;
                var profile = ArmorDyeCatalog.ForDonor(look.ItemId);
                if (profile != null)
                {
                    var regions = profile.RegionsFor(look.ItemId);
                    var colors = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int r = 0; r < regions.Count; r++)
                    {
                        colors[regions[r].Id] = RegionColors[r % RegionColors.Length];
                        row.Regions.Add((regions[r].Name, RegionColors[r % RegionColors.Length]));
                    }
                    row.Profile = profile.Id;
                    if (colors.Count > 0) palette = new ArmorDyePalette(profile.Id, profile.Revision, colors).Canonical();
                }

                // The craft, as the designer's Craft button does with this look and palette chosen.
                var recipe = new Recipe
                {
                    Family = "armor", Slot = look.Slot, Inner = "gambeson_quilted", Middle = "leather_middle", Outer = "leather_outer",
                    Extra = Recipe.None, SizePct = 100, AmmoKind = "none",
                };
                CraftOutcome? outcome = null;
                try
                {
                    outcome = CraftingExecutors.Current.Execute(cat, recipe, "{=}ML look " + look.ItemId, hero, skill, default(DonorVisual),
                        new NativeSmithingStaminaModel(), 0, new AppearanceIntent { DonorItemId = look.ItemId, DyePalette = palette });
                }
                catch (Exception ex) { row.Message = "craft threw " + ex.GetBaseException().Message; }
                if (outcome?.Item == null || !outcome.Success)
                {
                    row.Status = "craft failed"; row.Failed = true;
                    if (row.Message.Length == 0) row.Message = outcome?.Message ?? "no outcome";
                    Trace($"{n}/{looks.Count} {look.ItemId}: craft failed: {row.Message}");
                    continue;
                }
                var item = outcome.Item;
                row.Crafted = item.StringId;

                // The dye textures: rebuilt from the game's files and checked against MS2's manifest.
                if (palette != null)
                {
                    string? status = null;
                    bool ready = false;
                    yield return ctx.Until(() =>
                    {
                        ready = ArmorDyePreparation.Ready(palette, out status);
                        return ready || (status != null && !status.StartsWith("Preparing", StringComparison.Ordinal));
                    }, $"{look.ItemId}'s dye textures", TimeSpan.FromSeconds(ctx.ArgInt("readySeconds", 60)));
                    if (!ready)
                    {
                        row.Status = "dye failed"; row.Failed = true;
                        row.Message = FirstLine(status);
                        Trace($"{n}/{looks.Count} {look.ItemId}: dye failed: {row.Message}");
                    }
                    else row.Status = "dyed";
                }
                else row.Status = profile == null ? "no dye profile" : "no regions";

                if (itemView)
                {
                    // On its own, as the designer previews it, from four sides.
                    // High enough that the ground below is past the camera's far plane: only sky behind the piece.
                    using var render = LookRender.Show(agent.Mission.Scene, item, agent.Position + new Vec3(0f, 0f, ItemAltitude), lightIntensity, out var why);
                    if (render == null)
                    {
                        // No picture is a failed check: the person reading the sheet can't judge the colors.
                        row.Failed = true;
                        if (row.Status == "dyed") row.Status = "no render";
                        row.Message = (row.Message.Length > 0 ? row.Message + "; " : "") + why;
                        Trace($"{n}/{looks.Count} {look.ItemId}: {why}");
                    }
                    else
                    {
                        if (render.Note.Length > 0) Trace($"{look.ItemId}: {render.Note}");
                        foreach (var (angle, side) in new[] { (0f, "front"), (90f, "left"), (180f, "back"), (270f, "right") })
                        {
                            BallistaSteps.ViewFrom(agent.Mission.Scene, render.Eye(angle, 12f, ItemFov), render.Center, ItemFov);
                            var settle = DateTime.UtcNow.AddSeconds(0.6);
                            // A loading screen can come up over the scene (the first shots of a run caught it).
                            yield return ctx.Until(() => DateTime.UtcNow >= settle && !TaleWorlds.Engine.LoadingWindow.IsLoadingWindowActive, "the piece to show", TimeSpan.FromSeconds(60));
                            var shot = Shot(look.Slot + "-" + look.ItemId + "-" + side);
                            yield return ctx.Until(() => File.Exists(shot) && new FileInfo(shot).Length > 0, "the screenshot file", TimeSpan.FromSeconds(10));
                            var jpg = ToJpeg(shot, crop: true);
                            if (jpg != null) row.Shots.Add("shots/" + Path.GetFileName(jpg));
                        }
                    }
                }
                // On the hero, alone, photographed from the front and the back.
                else if (SlotIndex.TryGetValue(look.Slot, out var index))
                {
                    var eq = new Equipment(bare);
                    eq[index] = new EquipmentElement(item);
                    agent.UpdateSpawnEquipmentAndRefreshVisuals(eq);
                    var shot = SlotShot[look.Slot];
                    foreach (var (angle, side) in new[] { (25f, "front"), (205f, "back") })
                    {
                        BallistaSteps.ViewCamera(agent, angle, shot.Distance, shot.Height, shot.Rise, 0.75f);
                        var settle = DateTime.UtcNow.AddSeconds(0.6);
                        yield return ctx.Until(() => DateTime.UtcNow >= settle, "the piece to show");
                        var bmp = Shot($"{look.Slot}-{look.ItemId}-{side}");
                        yield return ctx.Until(() => File.Exists(bmp) && new FileInfo(bmp).Length > 0, "the screenshot file", TimeSpan.FromSeconds(10));
                        var jpg = ToJpeg(bmp, crop: false);
                        if (jpg != null) row.Shots.Add("shots/" + Path.GetFileName(jpg));
                    }
                }
                else row.Message = (row.Message.Length > 0 ? row.Message + "; " : "") + "not wearable by the hero (horse armor): crafted and dye-checked only (view item photographs it)";
                if (n % 25 == 0) Trace($"{n}/{looks.Count} done, {rows.Count(r => r.Failed)} failed so far");
            }
        }
        finally
        {
            (settings.RequireMaterials, settings.RequireStamina, settings.RequireUnlocks) = saved;
            if (Mission.Current?.Scene != null) BallistaSteps.ViewFrom(Mission.Current.Scene, null, Vec3.Zero);
            WriteSheet(folder, rows);
        }

        Mission.Current?.EndMission();
        yield return ctx.Until(() => Mission.Current == null, "the town center to close", TimeSpan.FromSeconds(60));

        var failed = rows.Where(r => r.Failed).ToList();
        var summary = string.Join(", ", rows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        Trace("done: " + summary);
        ctx.Report("looks", rows.Count);
        ctx.Report("failed", failed.Count);
        ctx.Report("value", summary);
        ctx.Dump("summary", summary + "\n" + string.Join("\n", failed.Select(r => $"{r.Status}: {r.Slot} {r.Id} ({r.Name}): {r.Message}")));
        ctx.Assert(failed.Count == 0, $"{failed.Count} of {rows.Count} look(s) failed: "
            + string.Join("; ", failed.Take(12).Select(r => $"{r.Id} {r.Status}")) + (failed.Count > 12 ? "; …" : "")
            + " (contact sheet: evidence\\every-look\\index.html)");
    }

    /// <summary>
    /// The looks a targeted run asks for: <c>ids</c> (comma list) and/or <c>idsFile</c> (one per line; # comments and
    /// blank lines skipped; environment variables expanded), each an item id or a dye profile id (every look using it).
    /// Null when neither is given (every look).
    /// </summary>
    private static HashSet<string>? WantedIds(StepContext ctx)
    {
        if (!ctx.HasArg("ids") && !ctx.HasArg("idsFile")) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ctx.Arg("ids", "").Split(',')) if (id.Trim().Length > 0) ids.Add(id.Trim());
        if (ctx.HasArg("idsFile"))
        {
            var path = Environment.ExpandEnvironmentVariables(ctx.Arg("idsFile"));
            if (!File.Exists(path)) throw new StepFailedException($"the list of looks {path} does not exist (one item or dye profile id per line)");
            foreach (var line in File.ReadAllLines(path))
            {
                var id = line.Split('#')[0].Trim();
                if (id.Length > 0) ids.Add(id);
            }
        }
        if (ids.Count == 0) throw new StepFailedException("ids/idsFile name no look");
        return ids;
    }

    private static int MenuOption(string id)
    {
        var menu = Campaign.Current?.CurrentMenuContext?.GameMenu;
        if (menu == null) return -1;
        int i = 0;
        foreach (var option in menu.MenuOptions)
        {
            if (option.IdString == id) return i;
            i++;
        }
        return -1;
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "no status";
        return line.Length > 300 ? line.Substring(0, 300) + "…" : line;
    }

    /// <summary>
    /// The screenshot as a JPEG beside it (a 1280x720 BMP is 2.7 MB; hundreds of them add up), the BMP removed. An item
    /// view is cropped to the square in the middle, where the camera framed the piece.
    /// </summary>
    private static string? ToJpeg(string bmp, bool crop)
    {
        var jpg = Path.ChangeExtension(bmp, ".jpg");
        try
        {
            using (var image = Image.FromFile(bmp))
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                using var quality = new EncoderParameters(1);
                quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                if (crop && image.Width > image.Height)
                {
                    int side = image.Height, x = (image.Width - side) / 2;
                    using var square = new Bitmap(side, side);
                    using (var g = Graphics.FromImage(square))
                        g.DrawImage(image, new Rectangle(0, 0, side, side), new Rectangle(x, 0, side, side), GraphicsUnit.Pixel);
                    square.Save(jpg, codec, quality);
                }
                else image.Save(jpg, codec, quality);
            }
            File.Delete(bmp);
            return jpg;
        }
        catch (Exception) { return File.Exists(bmp) ? bmp : null; }
    }

    /// <summary>evidence\every-look\index.html and results.json: failures first, then by slot.</summary>
    private static void WriteSheet(string folder, List<Row> rows)
    {
        string E(string s) => WebUtility.HtmlEncode(s ?? "");
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>MS2 armor looks</title><style>")
            .Append("body{font:14px system-ui,sans-serif;margin:16px;background:#f4f4f2;color:#1d1d1b}")
            .Append("h1{font-size:20px}h2{font-size:16px;margin-top:24px}.grid{display:flex;flex-wrap:wrap;gap:12px}")
            .Append(".card{background:#fff;border:1px solid #ccc;border-radius:6px;padding:8px;max-width:1040px}")
            .Append(".card.bad{border:2px solid #c0392b}.shots{display:flex;flex-wrap:wrap;gap:4px}.shots img{width:254px;height:auto;border-radius:3px}")
            .Append(".legend span{display:inline-flex;align-items:center;margin:2px 8px 2px 0}.legend i{width:14px;height:14px;border:1px solid #888;margin-right:4px;display:inline-block}")
            .Append(".meta{color:#555;font-size:12px}.msg{color:#c0392b;font-size:12px}")
            .Append("</style></head><body>");
        var failed = rows.Count(r => r.Failed);
        html.Append($"<h1>MS2 armor looks: {rows.Count} checked, {failed} failed</h1>");
        html.Append("<p class=\"meta\">").Append(E(string.Join(", ", rows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))))
            .Append(". Each dye region has its own color (legend under each look): a color that spills onto another part, or a part left its original color, is a wrong mask.</p>");
        foreach (var group in rows.OrderBy(r => r.Failed ? 0 : 1).ThenBy(r => r.Slot).GroupBy(r => r.Failed ? "Failed" : r.Slot))
        {
            html.Append("<h2>").Append(E(group.Key)).Append($" ({group.Count()})</h2><div class=\"grid\">");
            foreach (var r in group)
            {
                html.Append(r.Failed ? "<div class=\"card bad\">" : "<div class=\"card\">");
                html.Append("<div><b>").Append(E(r.Name)).Append("</b></div>");
                html.Append("<div class=\"meta\">").Append(E($"{r.Slot} · {r.Id} · {r.Status}" + (r.Profile.Length > 0 ? " · profile " + r.Profile : ""))).Append("</div>");
                if (r.Message.Length > 0) html.Append("<div class=\"msg\">").Append(E(r.Message)).Append("</div>");
                if (r.Regions.Count > 0)
                {
                    html.Append("<div class=\"legend\">");
                    foreach (var (name, color) in r.Regions) html.Append($"<span><i style=\"background:{color}\"></i>{E(name)}</span>");
                    html.Append("</div>");
                }
                if (r.Shots.Count > 0)
                {
                    html.Append("<div class=\"shots\">");
                    foreach (var s in r.Shots) html.Append($"<a href=\"{E(s)}\"><img loading=\"lazy\" src=\"{E(s)}\" alt=\"{E(r.Id)}\"></a>");
                    html.Append("</div>");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</body></html>");
        File.WriteAllText(Path.Combine(folder, "index.html"), html.ToString());

        var json = new StringBuilder("[\n");
        json.Append(string.Join(",\n", rows.Select(r =>
            "  {" + $"\"id\":{Q(r.Id)},\"name\":{Q(r.Name)},\"slot\":{Q(r.Slot)},\"profile\":{Q(r.Profile)},\"status\":{Q(r.Status)},\"failed\":{(r.Failed ? "true" : "false")},\"message\":{Q(r.Message)},\"crafted\":{Q(r.Crafted)},\"shots\":[{string.Join(",", r.Shots.Select(Q))}]" + "}")));
        json.Append("\n]\n");
        File.WriteAllText(Path.Combine(folder, "results.json"), json.ToString());
    }

    private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
}
