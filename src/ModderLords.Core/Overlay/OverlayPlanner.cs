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
    IReadOnlyList<string> Notes);

public sealed record OverlayPlan(string OverlayRoot, string EngineModulesRoot, IReadOnlyList<OverlayEntry> Entries);

/// <summary>
/// Decides, per selected mod, how the engine gets to see it. Reads directory listings, plus - only for a Run mod that
/// declares itself client-only - the IL metadata of its own submodule DLLs, to say whether overriding that declaration
/// is likely to survive. Tests inject <paramref name="scan"/> to keep the planner off disk entirely.
/// </summary>
public static class OverlayPlanner
{
    public static OverlayPlan Plan(string overlayRoot, string engineModulesRoot, IEnumerable<ModSelection> selections,
        Func<DiscoveredModule, ScanResult>? scan = null)
    {
        scan ??= AssemblyScan.Scan;
        var entries = new List<OverlayEntry>();
        foreach (var sel in selections)
        {
            var mod = sel.Module;
            var notes = new List<string>();
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
            if (sel.Role == ServerRole.Run && mod.HasHeadlessExclusions)
            {
                notes.Add("manifest tags mark it client-only; stripping them so the server loads it");
                // The mod said "not on a dedicated server" and Run overrides that. When its own code reaches for the
                // render stack there is nothing to override safely: the engine loads the DLL, the view assemblies get
                // pulled into a headless process and it dies without an exception. Say so before the launch, not after.
                var s = SafeScan(scan, mod);
                if (s is { Verdict: ServerVerdict.NeedsReview })
                {
                    var blockers = s.UiAssemblies.Concat(s.StoryModeAssemblies).Take(4).ToList();
                    notes.Add("WARNING: it declares itself client-only and its code references "
                              + string.Join(", ", blockers) + (s.UiAssemblies.Count + s.StoryModeAssemblies.Count > blockers.Count ? ", …" : "")
                              + " - Run is likely to crash the server. Consider DependencyOnly, which keeps its data and load-order entry without loading its code.");
                    foreach (var n in s.Notes) notes.Add("  " + n);
                }
            }
            if (sel.Role == ServerRole.DependencyOnly) notes.Add("dependency-only: kept in the module list for the handshake, no code loaded");

            var kind = (!manifestNeedsRewrite && (mod.HasServerBin || !mod.HasClientBin)) ? OverlayKind.DirectJunction : OverlayKind.Shadow;
            var shadow = kind == OverlayKind.Shadow ? Path.Combine(overlayRoot, mod.Id) : null;
            entries.Add(new OverlayEntry(sel, kind, enginePath, shadow, binTarget, notes));
        }
        return new OverlayPlan(overlayRoot, engineModulesRoot, entries);
    }

    /// <summary>A scan is a diagnostic; a mod with an unreadable DLL must still be able to launch.</summary>
    private static ScanResult? SafeScan(Func<DiscoveredModule, ScanResult> scan, DiscoveredModule mod)
    {
        try { return scan(mod); }
        catch { return null; }
    }
}
