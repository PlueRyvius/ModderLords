using System.Runtime.Versioning;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Launch;

/// <summary>
/// A private game view for ambiguous module IDs and custom roots. Bannerlord 1.4.8 resolves physical
/// Modules before Workshop and uses ../../ relative to its client bin. Every selected module therefore
/// gets one physical junction here. Original manifests and installations are untouched.
///
/// The exception is a mod with "server only" folders (<see cref="ClientLaunchPlan.ExcludedFolders"/>). A junction
/// is the whole folder or nothing, so that mod is a real folder here instead: one junction per top-level folder it
/// keeps, and copies of its top-level files. Nothing is copied out of the folders themselves.
///
/// Views outlive the launcher because the game is detached; never remove one when the launcher exits. Nothing in
/// the launcher deletes a view. Anything that ever does must unlink the junctions first (the ones directly under
/// Modules and the ones inside a mod's real folder) and must never delete recursively through one.
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
        {
            var target = Path.Combine(root, "Modules", mod.Id);
            var leftOut = LeftOut(plan, mod.Id);
            if (leftOut.Count == 0) Junction.Create(target, mod.FolderPath);
            else LinkWithout(target, mod.FolderPath, leftOut);
        }
        return root;
    }

    /// <summary>
    /// The names to leave out of one mod. Checked again here although the launch session already did: a plan can be
    /// built by hand, and a view without a mod's <c>bin</c> is a game that fails to start rather than a lighter one.
    /// </summary>
    private static IReadOnlyList<string> LeftOut(ClientLaunchPlan plan, string id) =>
        plan.ExcludedFolders.TryGetValue(id, out var names)
            ? names.Where(n => ServerFolderExclusions.Problem(n) is null).ToList()
            : [];

    [SupportedOSPlatform("windows")]
    private static void LinkWithout(string target, string modFolder, IReadOnlyList<string> leftOut)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(modFolder))
        {
            var name = Path.GetFileName(dir);
            if (leftOut.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Junction.Create(Path.Combine(target, name), dir);
        }
        // SubModule.xml and whatever else sits beside it (default configs, readmes). Copied rather than linked: a
        // hard link needs the same drive, and a view is thrown-away state made fresh for every launch.
        foreach (var file in Directory.EnumerateFiles(modFolder))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
}
