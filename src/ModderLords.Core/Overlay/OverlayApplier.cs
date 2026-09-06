using System.Runtime.Versioning;
using System.Text.Json;

namespace ModderLords.Core.Overlay;

/// <summary>
/// Materialises an OverlayPlan: builds shadow folders under our own overlay root and creates the junctions in
/// engine\Modules. Everything it creates inside engine\Modules is a junction and is recorded in a state file, so
/// a later sync can remove exactly those entries and nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayApplier
{
    private const string StateFileName = "overlay-state.json";

    public sealed record AppliedEntry(string ModuleId, string EngineModulePath, string Target, OverlayKind Kind, IReadOnlyList<string> ManifestChanges);
    public sealed record ApplyResult(IReadOnlyList<AppliedEntry> Applied, IReadOnlyList<string> Removed, IReadOnlyList<string> Warnings);

    private sealed class State
    {
        public List<string> EngineJunctions { get; set; } = new();
    }

    /// <summary>Submodule class types allowed to stay for DependencyOnly modules (e.g. MCM's settings core has no UI).</summary>
    public IReadOnlyCollection<string> KeepForDependencyOnly { get; init; } = Array.Empty<string>();

    public ApplyResult Apply(OverlayPlan plan)
    {
        Directory.CreateDirectory(plan.OverlayRoot);
        var state = LoadState(plan.OverlayRoot);
        var applied = new List<AppliedEntry>();
        var warnings = new List<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in plan.Entries)
        {
            var mod = e.Selection.Module;
            var manifestChanges = new List<string>();
            // One mod failing (locked file, odd manifest) must not abort the others or leave the state file stale.
            try
            {
                string target;
                if (e.Kind == OverlayKind.DirectJunction)
                {
                    target = mod.FolderPath;
                }
                else
                {
                    target = e.ShadowPath!;
                    manifestChanges.AddRange(BuildShadow(e, warnings));
                }

                // engine\Modules\<Id> must be a junction we own, or absent.
                if (Directory.Exists(e.EngineModulePath) && !Junction.IsJunction(e.EngineModulePath))
                {
                    warnings.Add($"{mod.Id}: engine\\Modules\\{mod.Id} is a real folder (not a junction); leaving it alone and NOT overlaying this mod");
                    continue;
                }
                Junction.Create(e.EngineModulePath, target);
                wanted.Add(e.EngineModulePath);
                if (!state.EngineJunctions.Contains(e.EngineModulePath, StringComparer.OrdinalIgnoreCase)) state.EngineJunctions.Add(e.EngineModulePath);
                applied.Add(new AppliedEntry(mod.Id, e.EngineModulePath, target, e.Kind, manifestChanges));
            }
            catch (Exception ex)
            {
                warnings.Add($"{mod.Id}: overlay failed and the mod was skipped: {ex.Message}");
                // A half-built engine link would make the engine see a broken module; drop it.
                try { if (Junction.IsJunction(e.EngineModulePath)) Junction.Remove(e.EngineModulePath); } catch { }
            }
        }

        // Remove junctions we created earlier that are no longer wanted.
        var removed = new List<string>();
        foreach (var old in state.EngineJunctions.ToList())
        {
            if (wanted.Contains(old)) continue;
            if (Directory.Exists(old) && Junction.IsJunction(old)) { Junction.Remove(old); removed.Add(old); }
            state.EngineJunctions.Remove(old);
        }

        SaveState(plan.OverlayRoot, state);
        return new ApplyResult(applied, removed, warnings);
    }

    /// <summary>Removes every junction recorded in the state file (used by "disable all" / uninstall).</summary>
    public IReadOnlyList<string> RemoveAll(string overlayRoot)
    {
        var state = LoadState(overlayRoot);
        var removed = new List<string>();
        foreach (var j in state.EngineJunctions)
            if (Directory.Exists(j) && Junction.IsJunction(j)) { Junction.Remove(j); removed.Add(j); }
        state.EngineJunctions.Clear();
        SaveState(overlayRoot, state);
        return removed;
    }

    private IEnumerable<string> BuildShadow(OverlayEntry e, List<string> warnings)
    {
        var mod = e.Selection.Module;
        var shadow = e.ShadowPath!;
        Directory.CreateDirectory(shadow);

        // 1. Rewritten manifest (a real file; Id/Version untouched).
        var original = File.ReadAllText(Path.Combine(mod.FolderPath, "SubModule.xml"));
        var rewritten = ManifestRewriter.Rewrite(original, e.Selection.Role, KeepForDependencyOnly);
        var manifestPath = Path.Combine(shadow, "SubModule.xml");
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != rewritten.Xml)
            File.WriteAllText(manifestPath, rewritten.Xml);

        // 2. bin\Win64_Shipping_Server -> the bin the engine should load from.
        var binDir = Path.Combine(shadow, "bin");
        Directory.CreateDirectory(binDir);
        var serverBinLink = Path.Combine(binDir, "Win64_Shipping_Server");
        if (Directory.Exists(e.BinTarget)) Junction.Create(serverBinLink, e.BinTarget);
        else if (Junction.IsJunction(serverBinLink)) Junction.Remove(serverBinLink);

        // 3. Every other top-level directory of the mod is junctioned as-is (ModuleData, GUI, AssetPackages, SceneObj ...).
        var wantedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" };
        foreach (var dir in Directory.EnumerateDirectories(mod.FolderPath))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
            wantedDirs.Add(name);
            Junction.Create(Path.Combine(shadow, name), dir);
        }
        foreach (var dir in Directory.EnumerateDirectories(shadow))
        {
            var name = Path.GetFileName(dir);
            if (!wantedDirs.Contains(name) && Junction.IsJunction(dir)) Junction.Remove(dir);
        }

        // 4. Other top-level files (mod-config defaults etc.) are copied when small; manifests are the only thing we rewrite.
        var wantedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SubModule.xml" };
        foreach (var file in Directory.EnumerateFiles(mod.FolderPath))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase)) continue;
            var fi = new FileInfo(file);
            if (fi.Length > 8 * 1024 * 1024) { warnings.Add($"{mod.Id}: top-level file {name} is larger than 8 MB and was not mirrored"); continue; }
            wantedFiles.Add(name);
            var dest = Path.Combine(shadow, name);
            if (!File.Exists(dest) || new FileInfo(dest).Length != fi.Length || File.GetLastWriteTimeUtc(dest) != fi.LastWriteTimeUtc)
            {
                File.Copy(file, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, fi.LastWriteTimeUtc);
            }
        }
        foreach (var file in Directory.EnumerateFiles(shadow))
            if (!wantedFiles.Contains(Path.GetFileName(file))) File.Delete(file);

        return rewritten.Changes;
    }

    private static State LoadState(string overlayRoot)
    {
        var p = Path.Combine(overlayRoot, StateFileName);
        if (!File.Exists(p)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(p)) ?? new State(); }
        catch { return new State(); }
    }

    private static void SaveState(string overlayRoot, State state)
    {
        var p = Path.Combine(overlayRoot, StateFileName);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, p, overwrite: true);
    }
}
