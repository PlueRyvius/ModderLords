using ModderLords.Core.Export;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Modules;

/// <summary>
/// Finds the one load order that crashes Bannerlord at startup: a mod that patches Coop placed before Coop.
///
/// Profiles saved by ModderLords 1.0.2 and earlier can hold it without the user ever having asked for it — saving in
/// Host mode walked the hidden Coop entry to the end of the list, and importing a shared list put it there too. The
/// check exists so the launcher can say so and offer to fix it, rather than repairing profiles behind the user's
/// back: Coop last is a perfectly good order for someone running no mods that patch it, and silently reordering
/// somebody's list is the very thing that caused this.
/// </summary>
public static class CoopOrderCheck
{
    /// <param name="CoopId">The Coop entry as this profile names it (Coop or CoopNightly).</param>
    /// <param name="ModsBeforeCoop">Enabled mods that patch Coop and are currently placed ahead of it.</param>
    public sealed record Finding(string CoopId, IReadOnlyList<string> ModsBeforeCoop)
    {
        /// <param name="manualLoadOrder">
        /// Whether the profile has "My order wins" ticked. It decides whether this is a crash or only a list that
        /// disagrees with what will load: under the default policy the sorter moves these mods after Coop by itself,
        /// so saying the game will crash would simply be untrue.
        /// </param>
        public string Message(bool manualLoadOrder)
        {
            var names = string.Join(", ", ModsBeforeCoop);
            var one = ModsBeforeCoop.Count == 1;
            return manualLoadOrder
                ? $"{names} load{(one ? "s" : "")} before {CoopId}, and My order wins is on, so that is the order the "
                  + $"game will get. {(one ? "It patches" : "They patch")} Coop and will crash Bannerlord at startup. "
                  + "Use Fix load order, or untick My order wins."
                : $"{names} {(one ? "is" : "are")} listed before {CoopId}. {(one ? "It patches" : "They patch")} Coop, so "
                  + $"ModderLords loads {(one ? "it" : "them")} after {CoopId} anyway — use Fix load order to make this "
                  + "list say what actually happens.";
        }
    }

    /// <summary>
    /// Null unless the profile actually holds the crashing arrangement: Coop present, and an enabled mod known to
    /// patch it sitting earlier in the list. Anything less is not a problem and must not be touched.
    /// </summary>
    /// <summary>
    /// A mod whose own submodule assembly binds to Coop, that nothing has placed after Coop. Safety otherwise rests
    /// entirely on somebody having curated a record for it, so an unknown mod fails open — it loads first and takes
    /// the game down. This catches it from the metadata instead.
    /// </summary>
    /// <param name="coopReferencingIds">Mods whose submodule DLLs reference a Coop assembly (from AssemblyScan).</param>
    /// <param name="declaresOrder">Whether the mod's manifest already asks to load after Coop.</param>
    public static IReadOnlyList<string> UnplacedCoopBinders(
        IReadOnlyList<ProfileMod> mods, IReadOnlyCollection<string> coopReferencingIds,
        IReadOnlyCollection<string> knownToFollowCoop, Func<string, bool> declaresOrder)
    {
        var coopAt = -1;
        for (var i = 0; i < mods.Count; i++)
            if (ClientManifest.CoopClientModuleIds.Contains(mods[i].Id)) { coopAt = i; break; }
        if (coopAt < 0) return [];

        return mods.Take(coopAt)
            .Where(m => m.Enabled
                        && coopReferencingIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase)
                        && !knownToFollowCoop.Contains(m.Id, StringComparer.OrdinalIgnoreCase)
                        && !declaresOrder(m.Id))
            .Select(m => m.Id)
            .ToList();
    }

    public static Finding? Inspect(IReadOnlyList<ProfileMod> mods, IReadOnlyCollection<string> knownToFollowCoop)
    {
        var coopIndex = -1;
        string? coopId = null;
        for (var i = 0; i < mods.Count; i++)
            if (ClientManifest.CoopClientModuleIds.Contains(mods[i].Id)) { coopIndex = i; coopId = mods[i].Id; break; }
        if (coopIndex < 0 || coopId is null) return null;

        var before = mods.Take(coopIndex)
            .Where(m => m.Enabled && knownToFollowCoop.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToList();

        return before.Count == 0 ? null : new Finding(coopId, before);
    }
}
