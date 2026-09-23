namespace ModderLords.Core.Saves;

/// <summary>
/// Compares the save a launch is about to load against the module set that launch will actually run.
///
/// A world carries the modules that built it. Loading one under a different set does not fail cleanly: Coop logs
/// "module mismatch ... Forcing load anyway", the object manager then cannot resolve what the removed modules
/// registered, and the campaign-load state machine stalls without ever saying why. This turns that into one line
/// before the engine starts. It <b>warns and never blocks</b> — saves legitimately drift, and refusing to launch
/// would break working setups.
/// </summary>
public static class SaveModuleCheck
{
    /// <summary>Modules the save has and the launch does not, and vice versa; version drift is reported separately.</summary>
    public sealed record Result(
        string SaveName,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> Added,
        IReadOnlyList<SaveHeaderReader.Diff> VersionChanges)
    {
        /// <summary>Only added/removed modules break a world. A version bump is worth saying, not worth alarm.</summary>
        public bool IsSevere => Removed.Count > 0 || Added.Count > 0;
        public bool IsClean => !IsSevere && VersionChanges.Count == 0;
    }

    /// <summary>
    /// Coop is a community module by <see cref="SaveHeader.CommunityModuleIds"/>'s definition but it is server
    /// infrastructure: it is stock in the server package, so it is in every save and in no profile's mod list. Left
    /// in, it would report "Coop is missing" on every launch and the warning would be trained away immediately.
    /// </summary>
    private static bool IsInfrastructure(string id) =>
        Export.ClientManifest.CoopClientModuleIds.Contains(id) || Export.ClientManifest.IsServerOnly(id)
        || id.StartsWith("ModderLords.", StringComparison.OrdinalIgnoreCase);

    public static Result Compare(SaveHeader save, IReadOnlyDictionary<string, string> plannedCommunityVersions)
    {
        var diffs = SaveHeaderReader.Compare(save, plannedCommunityVersions)
            .Where(d => !IsInfrastructure(d.ModuleId)).ToList();
        return new Result(
            save.Name,
            diffs.Where(d => d.Kind == "missing now").Select(d => d.ModuleId).ToList(),
            diffs.Where(d => d.Kind == "added").Select(d => d.ModuleId).ToList(),
            diffs.Where(d => d.Kind == "version changed").ToList());
    }

    /// <summary>
    /// Coop's autosave, and what it loads when no save is named. server-config.json's "saveName" being empty and no
    /// --save on the command line does <b>not</b> mean a fresh world: the regression launch of 2026-09-07 named no
    /// save and still reported save":"saveauto1". That is how a mismatched world got loaded by a command that never
    /// mentioned a save, so the check has to cover the implicit case or it misses its own motivating bug.
    /// </summary>
    public const string CoopAutoSaveName = "saveauto1";

    /// <summary>
    /// The messages for a launch, resolving the save Coop will actually load when none was named.
    ///
    /// <paramref name="templatePath"/> is the default_new_game.sav a missing save would be bootstrapped from. It is
    /// what makes this check useful before the save exists: a world that is about to be stamped out of the vanilla
    /// template is <b>already</b> mismatched against any modded launch, and saying so only after the file has been
    /// written means --dry-run — the one mode whose whole job is to report what a launch would do — stays silent
    /// about the launch's most likely failure. Pass null to skip the fresh-world check.
    /// </summary>
    public static IReadOnlyList<string> MessagesForLaunch(
        string savesDir,
        string? saveName,
        IReadOnlyDictionary<string, string> plannedCommunityVersions,
        string? templatePath = null,
        ISet<string>? mapModuleIds = null)
    {
        var implicitly_ = string.IsNullOrWhiteSpace(saveName);
        var name = implicitly_ ? CoopAutoSaveName : saveName!;
        var savePath = Path.Combine(savesDir, name + ".sav");
        var messages = File.Exists(savePath)
            ? Messages(savePath, plannedCommunityVersions, mapModuleIds)
            : FreshWorldMessages(name, templatePath, plannedCommunityVersions);
        if (messages.Count == 0 || !implicitly_) return messages;
        return messages.Select(m => m + $" (no save was named, so Coop will load its autosave '{name}')").ToList();
    }

    /// <summary>
    /// What a not-yet-created world will disagree about. The template lists Native;SandBoxCore;Sandbox;Coop and
    /// nothing else, so for any modded profile this fires every time — which is correct, and is the point. The
    /// existing post-creation warning tells you the world is wrong; this one tells you before anything is written,
    /// and names the two commands that actually produce a world built with the right modules, because "a world has
    /// to be created with the modules it will run under" is not an instruction anyone can act on by itself.
    /// </summary>
    public static IReadOnlyList<string> FreshWorldMessages(
        string saveName,
        string? templatePath,
        IReadOnlyDictionary<string, string> plannedCommunityVersions)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath)) return Array.Empty<string>();

        var header = SaveHeaderReader.TryRead(templatePath, out _);
        if (header is null) return Array.Empty<string>();

        var r = Compare(header, plannedCommunityVersions);
        if (!r.IsSevere) return Array.Empty<string>();

        // Only Added matters here. Removed would mean the vanilla template carries a community module this launch
        // drops, which cannot happen, and reporting it would be noise if the template is ever swapped.
        if (r.Added.Count == 0) return Array.Empty<string>();

        return new[]
        {
            $"WARNING save '{saveName}' does not exist and will be created from {Path.GetFileName(templatePath)}, " +
            $"which was built without {r.Added.Count} module(s) in this launch ({string.Join(", ", r.Added)}). " +
            "The engine will force the load and the campaign may stall during world init. " +
            "To get a world built with these modules: start a campaign in the real game with the same load order and " +
            "run 'import-save', or run 'create-world' to have the server build one."
        };
    }

    /// <summary>
    /// The launch messages for a save that already exists. Empty when the save is absent (a fresh world is about to
    /// be created from the template and there is nothing to disagree with), unreadable, or in agreement.
    /// </summary>
    public static IReadOnlyList<string> Messages(string savePath, IReadOnlyDictionary<string, string> plannedCommunityVersions,
        ISet<string>? mapModuleIds = null)
    {
        if (!File.Exists(savePath)) return Array.Empty<string>();
        var header = SaveHeaderReader.TryRead(savePath, out var error);
        if (header is null)
            return new[] { $"WARNING save '{Path.GetFileNameWithoutExtension(savePath)}' could not be read ({error}); its module list was not checked" };

        var r = Compare(header, plannedCommunityVersions);
        if (r.IsClean) return Array.Empty<string>();

        var messages = new List<string>();
        if (r.IsSevere)
        {
            var parts = new List<string>();
            if (r.Removed.Count > 0) parts.Add($"{r.Removed.Count} module(s) it was built with are not in this launch ({string.Join(", ", r.Removed)})");
            if (r.Added.Count > 0) parts.Add($"{r.Added.Count} module(s) in this launch are not in the save ({string.Join(", ", r.Added)})");
            messages.Add($"WARNING save '{r.SaveName}' does not match this module set: {string.Join("; ", parts)}. " +
                "The engine will force the load and the campaign may stall during world init. A world has to be created with the modules it will run under.");
        }
        foreach (var d in r.VersionChanges)
            messages.Add(VersionChangeLine(r.SaveName, d.ModuleId, d.SaveVersion, d.CurrentVersion,
                mapModuleIds?.Contains(d.ModuleId) == true));
        return messages;
    }

    /// <summary>
    /// One line per module whose version differs from the save's. A map-replacing mod gets a WARNING: its new version
    /// can ship a different campaign map (TAOM 2.0.27 -> 2.0.28 changed the navmesh), and an old world served on a new
    /// map crashed the server while it read the distance cache, or re-entered the map without end (2026-09-22). The
    /// launch still goes ahead: the maintainer's call is to warn, not refuse, since a version bump often changes nothing.
    /// </summary>
    public static string VersionChangeLine(string saveName, string moduleId, string saveVersion, string currentVersion, bool isMapMod) =>
        isMapMod
            ? $"WARNING save '{saveName}': map mod {moduleId} was {saveVersion} when this world was made, this launch has {currentVersion}. " +
              "A new version of a map mod can change the campaign map under an old world; if the server crashes while loading " +
              "or keeps reloading the map, create a new world. Launching anyway."
            : $"save '{saveName}': {moduleId} was {saveVersion}, this launch has {currentVersion}";
}
