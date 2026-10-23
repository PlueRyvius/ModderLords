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
/// The view mirrors Steam's layout: the game sits at &lt;id&gt;\common\&lt;game folder name&gt; and &lt;id&gt;\workshop is a junction to
/// the real steamapps\workshop, so a path counted up from the game's exe still reaches the Workshop. Reported
/// 2026-10-22: Fourberie opens "../../../../workshop/content/261550/2875710877/FourberieConfig.xml" from the exe folder,
/// and the client died at startup when that pointed past the view.
///
/// Views outlive the launcher because the game is detached; never remove one when the launcher exits. Nothing in
/// the launcher deletes a view. Anything that ever does must unlink the junctions first (the ones directly under
/// Modules, the ones inside a mod's real folder and the workshop one) and must never delete recursively through one.
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

        var gameRoot = Path.GetFullPath(plan.GameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gameFolder = Path.GetFileName(gameRoot) is { Length: > 0 } n ? n : "Mount & Blade II Bannerlord";
        var view = Path.Combine(Path.GetFullPath(viewsRoot), Guid.NewGuid().ToString("N"));
        var root = Path.Combine(view, "common", gameFolder);
        Directory.CreateDirectory(Path.Combine(root, "Modules"));
        // <game>\..\..\workshop is steamapps\workshop in a Steam install; elsewhere there is nothing to mirror.
        var workshop = Path.GetFullPath(Path.Combine(gameRoot, "..", "..", "workshop"));
        if (Directory.Exists(workshop)) Junction.Create(Path.Combine(view, "workshop"), workshop);
        foreach (var dir in Directory.EnumerateDirectories(plan.GameRoot))
        {
            var name = Path.GetFileName(dir);
            if (!name.Equals("Modules", StringComparison.OrdinalIgnoreCase)) Junction.Create(Path.Combine(root, name), dir);
        }
        foreach (var file in Directory.EnumerateFiles(plan.GameRoot))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var names = FolderNames(plan.SelectedModules);
        foreach (var mod in plan.SelectedModules)
        {
            var target = Path.Combine(root, "Modules", names[mod.Id]);
            var leftOut = LeftOut(plan, mod.Id);
            if (leftOut.Count == 0) Junction.Create(target, mod.FolderPath);
            else LinkWithout(target, mod.FolderPath, leftOut);
        }
        return root;
    }

    /// <summary>
    /// The folder each mod gets under the view's Modules: the name of the folder it is installed in, so the view looks
    /// to the mod exactly as a normal start does. Bannerlord finds a module by its SubModule.xml, whatever the folder
    /// is called, but some mods build paths from their own folder name. Reported 2026-10-15: ABUV Expanded loads its
    /// button pictures from "Modules\ABUV Expanded by FrannCC\..."; the view named that folder by the mod's ID
    /// (ABUVFiefBorders), so in co-op the buttons worked and showed no pictures.
    ///
    /// The ID is still used when the folder name cannot be one here: not a plain folder name, or the same as another
    /// selected mod's (two copies called "MyMod" from different places, which the ID tells apart).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> FolderNames(IReadOnlyList<Modules.DiscoveredModule> mods)
    {
        static bool Plain(string name) => name is not ("" or "." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        var folders = mods.ToDictionary(m => m.Id, m => m.FolderName, StringComparer.OrdinalIgnoreCase);
        var shared = folders.Values.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            names[mod.Id] = Plain(folders[mod.Id]) && !shared.Contains(folders[mod.Id]) ? folders[mod.Id] : mod.Id;
        // A fallback ID can still meet another mod's real folder name (mod "B" installed in a folder called "A", and
        // mod "A" falling back to its ID). Then every mod is named by its ID, as views always were: those are unique.
        if (names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            foreach (var mod in mods) names[mod.Id] = mod.Id;
        return names;
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
