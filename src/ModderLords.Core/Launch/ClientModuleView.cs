using System.Runtime.Versioning;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Launch;

/// <summary>
/// A private game view for ambiguous module IDs and custom roots. Bannerlord 1.4.8 resolves physical
/// Modules before Workshop and uses ../../ relative to its client bin. Every selected module therefore
/// gets one physical junction here. Original manifests and installations are untouched.
/// Views outlive the launcher because the game is detached; never remove one when the launcher exits.
/// </summary>
public static class ClientModuleView
{
    [SupportedOSPlatform("windows")]
    public static string Create(ClientLaunchPlan plan, string viewsRoot)
    {
        // Validate before creating anything. IDs become directory names, never relative paths.
        foreach (var mod in plan.SelectedModules)
        {
            if (mod.Id is "" or "." or ".." || mod.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"Cannot link invalid module ID: {mod.Id}");
            if (!File.Exists(Path.Combine(mod.FolderPath, "SubModule.xml")))
                throw new InvalidOperationException($"Selected copy is no longer installed: {mod.Id} ({mod.FolderPath}). Rescan before launching.");
        }
        if (plan.SelectedModules.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.SelectedModules.Count)
            throw new InvalidOperationException("Only one copy of each module can be linked for launch.");

        var root = Path.Combine(Path.GetFullPath(viewsRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Modules"));
        foreach (var dir in Directory.EnumerateDirectories(plan.GameRoot))
        {
            var name = Path.GetFileName(dir);
            if (!name.Equals("Modules", StringComparison.OrdinalIgnoreCase)) Junction.Create(Path.Combine(root, name), dir);
        }
        foreach (var file in Directory.EnumerateFiles(plan.GameRoot))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        foreach (var mod in plan.SelectedModules)
            Junction.Create(Path.Combine(root, "Modules", mod.Id), mod.FolderPath);
        return root;
    }
}
