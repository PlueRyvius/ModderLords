using ModderLords.Core.Compat;
using ModderLords.Core.Modules;

namespace ModderLords.Core.Overlay;

/// <summary>One community module the user wants on the server, with the source folder chosen for it.</summary>
public sealed record ModSelection(DiscoveredModule Module, ServerRole Role);

public enum OverlayKind
{
    /// <summary>engine\Modules\Id -> mod folder directly (mod already ships a server bin and needs no manifest changes).</summary>
    DirectJunction,
    /// <summary>engine\Modules\Id -> shadow folder (rewritten SubModule.xml + junctions into the mod folder).</summary>
    Shadow,
}

public sealed record OverlayEntry(
    ModSelection Selection,
    OverlayKind Kind,
    string EngineModulePath,
    string? ShadowPath,
    string BinTarget,
    IReadOnlyList<string> Notes,
    string? HeadlessAssetPath = null,
    string? HeadlessMapPath = null);

public sealed record OverlayPlan(string OverlayRoot, string EngineModulesRoot, IReadOnlyList<OverlayEntry> Entries);

/// <summary>
/// Decides, per selected mod, how the engine gets to see it. Reads directory listings, plus - only for a Run mod that
/// declares itself client-only - the IL metadata of its own submodule DLLs, to say whether overriding that declaration
/// is likely to survive. Tests inject <paramref name="scan"/> to keep the planner off disk entirely.
/// </summary>
public static class OverlayPlanner
{
    public static OverlayPlan Plan(string overlayRoot, string engineModulesRoot, IEnumerable<ModSelection> selections,
        Func<DiscoveredModule, ScanResult>? scan = null, Func<string, CompatRecord?>? record = null,
        IReadOnlyDictionary<string, string>? headlessAssetPaths = null,
        IReadOnlyDictionary<string, string>? headlessMapPaths = null,
        Func<DiscoveredModule, HashSet<string>>? typeNames = null)
    {
        scan ??= AssemblyScan.Scan;
        record ??= CompatDb.Current.Find;
        typeNames ??= AssemblyScan.ModuleTypeNames;
        var entries = new List<OverlayEntry>();
        foreach (var original in selections)
        {
            var mod = original.Module;
            var notes = new List<string>();

            // The role is settled HERE, before anything reads it. A downgrade decided further down would be silently
            // wrong: manifestNeedsRewrite and kind below are both derived from the role, so a role that changed after
            // them would build an overlay for the role we had just rejected.
            var sel = ResolveRole(original, scan, record, typeNames, notes);

            var enginePath = Path.Combine(engineModulesRoot, mod.Id);

            var binTarget = mod.HasServerBin ? mod.ServerBin : mod.ClientBin;
            if (!mod.HasServerBin && mod.HasClientBin) notes.Add("no Win64_Shipping_Server bin; exposing Win64_Shipping_Client as the server bin");
            if (!mod.HasServerBin && !mod.HasClientBin) notes.Add("no managed bin at all (data-only module)");

            bool manifestNeedsRewrite = sel.Role switch
            {
                ServerRole.Run => mod.HasHeadlessExclusions,
                ServerRole.DependencyOnly => mod.HasCode,
                _ => false,
            };
            if (sel.Role == ServerRole.DependencyOnly) notes.Add("dependency-only: kept in the module list for the handshake, no code loaded");

            string? headlessAssetPath = null;
            string? headlessMapPath = null;
            headlessAssetPaths?.TryGetValue(mod.Id, out headlessAssetPath);
            headlessMapPaths?.TryGetValue(mod.Id, out headlessMapPath);
            if (headlessAssetPath is not null) notes.Add("server simulation assets prepared in the private launch overlay");
            if (headlessMapPath is not null) notes.Add("server map projection prepared in the private launch overlay");
            var kind = (!manifestNeedsRewrite && headlessAssetPath is null && headlessMapPath is null &&
                        (mod.HasServerBin || !mod.HasClientBin)) ? OverlayKind.DirectJunction : OverlayKind.Shadow;
            var shadow = kind == OverlayKind.Shadow ? Path.Combine(overlayRoot, mod.Id) : null;
            entries.Add(new OverlayEntry(sel, kind, enginePath, shadow, binTarget, notes, headlessAssetPath, headlessMapPath));
        }
        return new OverlayPlan(overlayRoot, engineModulesRoot, entries);
    }

    /// <summary>
    /// The role this mod will actually launch under, plus any notes explaining a change. Extracted so the decision is
    /// made once, up front, and every later use reads a single value.
    /// </summary>
    private static ModSelection ResolveRole(ModSelection sel, Func<DiscoveredModule, ScanResult> scan,
        Func<string, CompatRecord?> record, Func<DiscoveredModule, HashSet<string>> typeNames, List<string> notes)
    {
        var mod = sel.Module;
        if (sel.Role != ServerRole.Run) return sel;

        // A curated record that says Run is a tested result and outranks every static guess below, so when we have
        // one there is nothing to scan for. Scanned once and shared: the desktop check and the view-assembly check
        // both want the same result.
        var vouched = VouchedForRunning(record, mod.Id);
        var scanned = vouched ? null : SafeScan(scan, mod);

        if (mod.HasHeadlessExclusions)
        {
            // The hard failure first. The submodule that survives a Run rewrite names a class, and when that class is
            // not in the mod's DLLs the engine dies on a missing SubModuleClassType - a native access violation with
            // no managed exception to read (FamilyAppearanceEditor, 2026-09-19).
            //
            // The fallback is DependencyOnly and NOT AsShipped. AsShipped leaves DedicatedServerType=custom in place,
            // so the engine loads that same missing class and dies exactly as before; it only looks safer.
            // DependencyOnly drops the submodules while keeping id and version for Coop's handshake, which is what
            // was observed to actually reach SERVING. Do not "simplify" this to AsShipped.
            var missing = MissingServerSubmoduleClasses(mod, typeNames);
            if (missing.Count > 0)
            {
                notes.Add("WARNING: its manifest names server submodule class(es) " + string.Join(", ", missing)
                          + " which are not in this mod's DLLs; Run would crash the server on load. "
                          + "Falling back to DependencyOnly: data and load-order entry kept, code not loaded.");
                return sel with { Role = ServerRole.DependencyOnly };
            }
        }

        // Desktop frameworks, deliberately NOT gated on HasHeadlessExclusions. A WinForms or WPF reference kills the
        // server whether or not the mod bothered to tag itself client-only, because the assembly is absent from the
        // server's runtime rather than merely unsafe to touch - so the method holding the call site cannot even be
        // compiled headless. Gating this on the manifest would miss every mod that forgot the tag.
        var desktop = scanned?.DesktopAssemblies ?? [];
        if (desktop.Count > 0)
            notes.Add("WARNING: its code references " + string.Join(", ", desktop)
                      + ", which the dedicated server's runtime does not ship - Run will crash the server on load, "
                      + "not only if the feature is used. Use AsShipped to let the engine skip it, or DependencyOnly "
                      + "to keep its data and load-order entry without loading its code.");

        if (!mod.HasHeadlessExclusions) return sel;

        notes.Add("manifest tags mark it client-only; stripping them so the server loads it");
        // The mod said "not on a dedicated server" and Run overrides that. When its own code reaches for the
        // render stack there is nothing to override safely: the engine loads the DLL, the view assemblies get
        // pulled into a headless process and it dies without an exception. Say so before the launch, not after.
        //
        // Plenty of mods reference the view assemblies from code paths a headless server never enters, and the
        // bundled guards cover the common entry points - ImprovedGarrisons is the worked example.
        if (scanned is { Verdict: ServerVerdict.NeedsReview })
        {
            // Can be empty when the only finding was a desktop framework, which already has its own line above.
            var blockers = scanned.UiAssemblies.Concat(scanned.StoryModeAssemblies).Take(4).ToList();
            if (blockers.Count > 0)
            {
                notes.Add("WARNING: it declares itself client-only and its code references "
                          + string.Join(", ", blockers) + (scanned.UiAssemblies.Count + scanned.StoryModeAssemblies.Count > blockers.Count ? ", ..." : "")
                          + " - Run may crash the server. Consider DependencyOnly, which keeps its data and load-order entry without loading its code.");
                foreach (var n in scanned.Notes) notes.Add("  " + n);
            }
        }
        return sel;
    }

    /// <summary>
    /// Server submodule classes the manifest promises but the DLLs do not define.
    ///
    /// Mirrors the "which submodule survives Run" rule in <see cref="ManifestRewriter"/> - deliberately, because the
    /// two read different representations (raw XML there, BUTR's parsed model here) and making one depend on the
    /// other would drag DiscoveredModule into what is otherwise a pure XML transform. Change the rule in one, change
    /// it in the other.
    /// </summary>
    private static IReadOnlyList<string> MissingServerSubmoduleClasses(DiscoveredModule mod, Func<DiscoveredModule, HashSet<string>> typeNames)
    {
        var subs = mod.Info.SubModules;
        var hasServerVariant = subs.Any(s => DedicatedServerType(s) is { } v && !IsNone(v));

        // Only a mod that actually split itself is at risk. With no server variant, Run strips tags and keeps the one
        // submodule the mod has always used on clients - nothing new to verify, and nothing to gain by opening DLLs.
        if (!hasServerVariant) return [];

        var surviving = subs
            .Where(s => DedicatedServerType(s) is not { } v || !IsNone(v))
            .Select(s => s.SubModuleClassType)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (surviving.Count == 0) return [];

        HashSet<string> defined;
        try { defined = typeNames(mod); }
        catch { return []; }               // unreadable DLLs: this is a guard, not a gate - never block a launch on it
        if (defined.Count == 0) return [];  // nothing readable to prove absence against, so prove nothing

        return surviving.Where(t => !defined.Contains(t)).ToList();

        static string? DedicatedServerType(Bannerlord.ModuleManager.SubModuleInfoExtended s) =>
            s.Tags.TryGetValue("DedicatedServerType", out var v) ? v.FirstOrDefault() : null;

        static bool IsNone(string v) => v.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the compat database has seen this mod run on the server, which beats any static guess.</summary>
    private static bool VouchedForRunning(Func<string, CompatRecord?> record, string id)
    {
        var r = record(id);
        return r is { DefaultRole: ServerRole.Run } && r.Verdict is CompatVerdict.Works or CompatVerdict.NeedsRecipe;
    }

    /// <summary>A scan is a diagnostic; a mod with an unreadable DLL must still be able to launch.</summary>
    private static ScanResult? SafeScan(Func<DiscoveredModule, ScanResult> scan, DiscoveredModule mod)
    {
        try { return scan(mod); }
        catch { return null; }
    }
}
