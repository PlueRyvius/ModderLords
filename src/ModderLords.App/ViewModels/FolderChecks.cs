using System.IO;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;

namespace ModderLords.App.ViewModels;

/// <summary>
/// The plain-language checks the Folders dialog shows under each box. Kept out of the window so they can be tested
/// without one, and so the wording lives in one place.
/// </summary>
public static class FolderChecks
{
    public const string ModRootsHint =
        "Folders that hold mods outside the game's Modules folder and the Steam Workshop. Pick the folder that CONTAINS " +
        "the mods (each mod is a folder inside it with its own SubModule.xml); picking a mod itself adds the folder it sits in.";

    /// <param name="root">The folder typed or browsed to, or null for automatic.</param>
    /// <param name="detected">Where automatic would point, or null when nothing was found.</param>
    public static (bool Ok, string Text) Game(string? root, string? detected)
    {
        if (root is null)
            return detected is null
                ? (false, "Not found automatically. Browse to your Bannerlord folder (the one containing bin and Modules).")
                : (true, "Automatic: " + detected);
        if (!Directory.Exists(root)) return (false, "This folder does not exist.");
        return File.Exists(Path.Combine(GamePaths.ClientBin(root), "Bannerlord.exe"))
            ? (true, "OK: Bannerlord found.")
            : (false, @"Bannerlord.exe is not under bin\Win64_Shipping_Client here. Pick the game folder itself, the one containing bin and Modules.");
    }

    public static (bool Ok, string Text) Server(string? root, string? detected)
    {
        if (root is null)
            return detected is null
                ? (false, "Not found automatically. Subscribe to Bannerlord Coop on the Steam Workshop, or browse to its DedicatedServer folder.")
                : (true, "Automatic: " + detected);
        if (!Directory.Exists(root)) return (false, "This folder does not exist.");
        var problems = ServerPaths.Create(root).Validate().ToList();
        return problems.Count == 0
            ? (true, "OK: the Coop dedicated server is complete.")
            : (false, problems[0] + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : ""));
    }

    /// <summary>
    /// The catalogue scans the folders INSIDE an extra mod folder, so a picked folder that is itself a mod (it has a
    /// SubModule.xml) would find nothing. Its parent is what was meant.
    /// </summary>
    public static string ModRootFor(string picked) =>
        File.Exists(Path.Combine(picked, "SubModule.xml")) && Path.GetDirectoryName(picked) is { Length: > 0 } parent
            ? parent
            : picked;
}
