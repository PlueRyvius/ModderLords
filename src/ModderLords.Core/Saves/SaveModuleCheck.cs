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

    /// <summary>The messages for a launch, resolving the save Coop will actually load when none was named.</summary>
    public static IReadOnlyList<string> MessagesForLaunch(string savesDir, string? saveName, IReadOnlyDictionary<string, string> plannedCommunityVersions)
    {
        var implicitly_ = string.IsNullOrWhiteSpace(saveName);
        var name = implicitly_ ? CoopAutoSaveName : saveName!;
        var messages = Messages(Path.Combine(savesDir, name + ".sav"), plannedCommunityVersions);
        if (messages.Count == 0 || !implicitly_) return messages;
        return messages.Select(m => m + $" (no save was named, so Coop will load its autosave '{name}')").ToList();
    }

    /// <summary>
    /// The launch messages for a save that already exists. Empty when the save is absent (a fresh world is about to
    /// be created from the template and there is nothing to disagree with), unreadable, or in agreement.
    /// </summary>
    public static IReadOnlyList<string> Messages(string savePath, IReadOnlyDictionary<string, string> plannedCommunityVersions)
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
            messages.Add($"save '{r.SaveName}': {d.ModuleId} was {d.SaveVersion}, this launch has {d.CurrentVersion}");
        return messages;
    }
}
