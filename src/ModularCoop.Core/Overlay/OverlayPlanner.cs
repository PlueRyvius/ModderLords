using ModularCoop.Core.Modules;

namespace ModularCoop.Core.Overlay;

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

/// <summary>Decides, per selected mod, how the engine gets to see it. Pure: touches no disk beyond reading directory listings.</summary>
public static class OverlayPlanner
{
    public static OverlayPlan Plan(string overlayRoot, string engineModulesRoot, IEnumerable<ModSelection> selections)
    {
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
            if (sel.Role == ServerRole.Run && mod.HasHeadlessExclusions) notes.Add("manifest tags mark it client-only; stripping them so the server loads it");
            if (sel.Role == ServerRole.DependencyOnly) notes.Add("dependency-only: kept in the module list for the handshake, no code loaded");

            var kind = (!manifestNeedsRewrite && (mod.HasServerBin || !mod.HasClientBin)) ? OverlayKind.DirectJunction : OverlayKind.Shadow;
            var shadow = kind == OverlayKind.Shadow ? Path.Combine(overlayRoot, mod.Id) : null;
            entries.Add(new OverlayEntry(sel, kind, enginePath, shadow, binTarget, notes));
        }
        return new OverlayPlan(overlayRoot, engineModulesRoot, entries);
    }
}
