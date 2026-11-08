using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.Core;
using TaleWorlds.ModuleManager;

namespace ModderLords.Testing;

/// <summary>
/// Steps that look for one side quietly differing from the other: a mod that loaded but never started, and a hero
/// whose stats, perks or health are not the same on the server and the player's game.
/// </summary>
internal static class ParitySteps
{
    internal static void Register()
    {
        InGameTests.Register("mods.started", StepSide.Any, ModsStarted);
        InGameTests.Register("hero.parity", StepSide.Any, HeroParity);
    }

    // ---- mods.started ----------------------------------------------------------------------------------------

    /// <summary>
    /// Every community mod whose code loaded must show it started: a Harmony patch, a campaign behaviour or a game
    /// model of its own. A mod can load its DLL, fail inside its own start-up, catch the error and go quiet (Both Perks
    /// on a dedicated server: it cannot read the game version there). value is the quiet mods, or "none".
    /// Args: <c>ignore</c>, comma-separated module ids that are allowed to show nothing.
    /// </summary>
    private static IEnumerable<StepWait?> ModsStarted(StepContext ctx)
    {
        yield return ctx.Until(() => Campaign.Current != null, "the campaign");
        var ignore = new HashSet<string>(ctx.Arg("ignore", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // DedicatedServer.Windows is TaleWorlds' own server module, not marked official, with nothing to start. On the
        // server ModderLords keeps only MCM's settings core (CompatDb.FallbackKeepSubModules), which patches nothing.
        ignore.Add("DedicatedServer.Windows");
        if (ctx.IsServer) ignore.Add("Bannerlord.MBOptionScreen");
        var modules = ModuleHelper.GetActiveModules().Where(m => !m.IsOfficial).ToList();
        var owners = new AssemblyOwners(modules);
        string? Owner(Type? t) => t == null ? null : owners.Of(t.Assembly);

        var evidence = modules.ToDictionary(m => m.Id, _ => new Evidence(), StringComparer.Ordinal);
        var allPatches = HarmonyPatches().ToList();
        ctx.Report("patches", allPatches.Count);
        foreach (var (patch, target) in allPatches)
            if (Owner(patch.DeclaringType) is { } id) evidence[id].Patches.Add(target);
        foreach (var behaviour in Campaign.Current.CampaignBehaviorManager.GetBehaviors<CampaignBehaviorBase>())
            if (Owner(behaviour.GetType()) is { } id) evidence[id].Behaviours.Add(behaviour.GetType().FullName);
        foreach (var model in Models())
            if (Owner(model.GetType()) is { } id) evidence[id].Models.Add(model.GetType().FullName);

        var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name), StringComparer.OrdinalIgnoreCase);
        var dump = new StringBuilder();
        var quiet = new List<string>();
        foreach (var module in modules)
        {
            var dlls = module.SubModules.Select(s => s.DLLName).Where(n => !string.IsNullOrEmpty(n)).Select(Path.GetFileNameWithoutExtension).Distinct().ToList();
            var e = evidence[module.Id];
            var codeLoaded = dlls.Any(loaded.Contains);
            dump.AppendLine($"{module.Id}: dlls [{string.Join(", ", dlls)}] loaded={codeLoaded} patches={e.Patches.Count} behaviours={e.Behaviours.Count} models={e.Models.Count}");
            foreach (var b in e.Behaviours) dump.AppendLine("  behaviour " + b);
            foreach (var m in e.Models) dump.AppendLine("  model " + m);
            foreach (var p in e.Patches.Distinct().Take(50)) dump.AppendLine("  patches " + p);
            if (!codeLoaded || e.Any) continue;
            if (ignore.Contains(module.Id)) { ctx.Log($"{module.Id} shows no sign it started (ignored by the suite)"); continue; }
            quiet.Add(module.Id);
        }
        ctx.Dump("mods-started", dump.ToString());
        ctx.Report("checked", modules.Count);
        ctx.Report("value", quiet.Count == 0 ? "none" : string.Join(",", quiet));
        ctx.Assert(quiet.Count == 0, $"loaded but never started (no patch, campaign behaviour or model of its own): {string.Join(", ", quiet)}; see mods-started.txt");
    }

    private sealed class Evidence
    {
        public readonly List<string> Patches = new List<string>();
        public readonly List<string> Behaviours = new List<string>();
        public readonly List<string> Models = new List<string>();
        public bool Any => Patches.Count + Behaviours.Count + Models.Count > 0;
    }

    /// <summary>
    /// Which community module an assembly belongs to. First by where it was loaded from: inside a module's folder is
    /// that module's; anywhere else under the game (its bin, the official modules) is the game's, even when a mod ships
    /// a copy (Coop carries game DLLs). An assembly loaded from elsewhere (the server's resolver loads mods' DLLs from
    /// the Workshop; ModderLords loads its Coop adapter by hand) goes by file name: every DLL under a module's folder
    /// counts (Both Perks loads its core from compat\v1_x), and a name more than one module ships belongs to none.
    /// </summary>
    private sealed class AssemblyOwners
    {
        private readonly List<(string Folder, string Id)> _folders;
        private readonly Dictionary<string, string?> _byName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private readonly string _game = Full(TaleWorlds.Library.BasePath.Name);

        public AssemblyOwners(IEnumerable<ModuleInfo> modules)
        {
            _folders = modules.Select(m => (Full(m.FolderPath), m.Id)).OrderByDescending(f => f.Item1.Length).ToList();
            foreach (var (folder, id) in _folders)
            {
                List<string> files;
                try { files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).ToList(); }
                catch { continue; }
                foreach (var name in files.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
                    _byName[name] = _byName.TryGetValue(name, out var other) && other != id ? null : id;
            }
        }

        public string? Of(Assembly assembly)
        {
            string location;
            try { location = assembly.IsDynamic ? "" : assembly.Location; }
            catch { location = ""; }
            if (location.Length > 0)
            {
                var path = Full(location);
                foreach (var (folder, id) in _folders)
                    if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return id;
                if (_game.Length > 0 && path.StartsWith(_game, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return _byName.TryGetValue(assembly.GetName().Name, out var owner) ? owner : null;
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/') + "\\"; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Every Harmony patch method in this game with the method it patches. Each Harmony copy loaded (Bannerlord.Harmony's,
    /// one a mod bundles) keeps its own patch list, so all are read. Reflection: this module ships no Harmony.
    /// </summary>
    private static IEnumerable<(MethodInfo Patch, string Target)> HarmonyPatches()
    {
        foreach (var harmony in AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony", false)).Where(t => t != null))
        {
            var all = harmony!.GetMethod("GetAllPatchedMethods", BindingFlags.Public | BindingFlags.Static);
            var info = harmony.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static);
            if (all == null || info == null) continue;
            foreach (var original in ((IEnumerable)all.Invoke(null, null)).Cast<MethodBase>().ToList())
            {
                var patches = info.Invoke(null, new object[] { original });
                if (patches == null) continue;
                var target = $"{original.DeclaringType?.FullName}.{original.Name}";
                // Harmony's Patches holds its lists in public readonly fields, not properties.
                foreach (var kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                    foreach (var patch in (IEnumerable?)patches.GetType().GetField(kind)?.GetValue(patches) ?? Array.Empty<object>())
                        if (patch.GetType().GetProperty("PatchMethod")?.GetValue(patch, null) is MethodInfo method)
                            yield return (method, target);
            }
        }
    }

    /// <summary>The game's current models (Campaign.Current.Models and Game.Current.BasicModels), whatever mod set them.</summary>
    private static IEnumerable<object> Models()
    {
        foreach (var holder in new object?[] { Campaign.Current?.Models, Game.Current?.BasicModels })
        {
            if (holder == null) continue;
            foreach (var property in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !typeof(GameModel).IsAssignableFrom(property.PropertyType)) continue;
                object? model;
                try { model = property.GetValue(holder, null); }
                catch { continue; }
                if (model != null) yield return model;
            }
        }
    }

    // ---- hero.parity -----------------------------------------------------------------------------------------

    /// <summary>
    /// A hero as this side sees it, in one line a compare can check: level, health and its maximum, gold, attributes,
    /// skills, focus and perks. Each list is summed and hashed so a different set with the same count still differs;
    /// the full values are in hero-parity-SIDE.txt for the diff. Args: <c>heroName</c> or <c>hero</c>.
    /// </summary>
    private static IEnumerable<StepWait?> HeroParity(StepContext ctx)
    {
        var hero = BuiltInSteps.FindHero(ctx);
        yield return ctx.Settled(() => Summary(hero, out _), $"{hero.StringId}'s state", timeout: TimeSpan.FromSeconds(30));
        var value = Summary(hero, out var detail);
        ctx.Dump("hero-parity-" + ctx.Side.Replace(':', '-'), detail);
        ctx.Report("maxHp", hero.MaxHitPoints);
        ctx.Report("hp", hero.HitPoints);
        ctx.Report("value", value);
    }

    private static string Summary(Hero hero, out string detail)
    {
        var attributes = Attributes.All.Select(a => $"{a.StringId}={hero.GetAttributeValue(a)}").ToList();
        var skills = Skills.All.Select(s => (s, v: hero.GetSkillValue(s))).Where(x => x.v != 0).Select(x => $"{x.s.StringId}={x.v}").ToList();
        var focus = Skills.All.Select(s => (s, v: hero.HeroDeveloper?.GetFocus(s) ?? 0)).Where(x => x.v != 0).Select(x => $"{x.s.StringId}={x.v}").ToList();
        var perks = PerkObject.All.Where(hero.GetPerkValue).Select(p => p.StringId).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var text = new StringBuilder();
        text.AppendLine($"hero {hero.StringId} ({hero.Name})");
        text.AppendLine($"level {hero.Level}, hp {hero.HitPoints}/{hero.MaxHitPoints}, gold {hero.Gold}");
        text.AppendLine("attributes: " + string.Join(", ", attributes));
        text.AppendLine("skills: " + string.Join(", ", skills));
        text.AppendLine("focus: " + string.Join(", ", focus));
        text.AppendLine($"perks ({perks.Count}):");
        foreach (var p in perks) text.AppendLine("  " + p);
        detail = text.ToString();

        return $"level {hero.Level}, hp {hero.HitPoints}/{hero.MaxHitPoints}, gold {hero.Gold}, attributes {Hash(attributes)}, "
               + $"skills {skills.Count} {Hash(skills)}, focus {focus.Count} {Hash(focus)}, perks {perks.Count} {Hash(perks)}";
    }

    /// <summary>Six hex digits of SHA-1: short enough to read in the report, long enough that two different lists differ.</summary>
    private static string Hash(IEnumerable<string> items)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", items)));
        return "#" + BitConverter.ToString(bytes, 0, 3).Replace("-", "").ToLowerInvariant();
    }
}
