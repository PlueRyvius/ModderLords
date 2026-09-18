using Bannerlord.ModuleManager;
using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Export;

/// <summary>
/// The order a mod list has on a PLAYER's machine, computed the same way whichever mode asked for it.
///
/// A host and a player looking at one profile were producing two different orders, because each exported the engine
/// order of the game it was about to launch: the dedicated server pins Coop after the community block, so a host's
/// export said "Coop last" — which is true of the server and wrong of every client that imported it. Coop's position
/// on a player's machine is a real choice (mods that patch it crash the game if they load first), so it is the one
/// an exported list has to carry.
/// </summary>
public static class ClientOrder
{
    /// <summary>
    /// Positions for the mods in a shared list. The result is the client order, so it is identical whether the
    /// profile was prepared for hosting or for playing.
    /// </summary>
    /// <param name="prepared">The selection this export is being made from, in either mode.</param>
    /// <param name="profile">Carries the user's preferred order, and Coop's place in it.</param>
    public static IReadOnlyList<string> For(ModuleSelectionResult prepared, Profile profile,
                                            IReadOnlyCollection<string>? knownToFollowCoop = null)
    {
        var mods = prepared.Selections.Select(s => s.Module)
            .Where(m => !ClientManifest.IsServerOnly(m.Id))
            .ToList();

        // In Host mode Coop is the server's own stock module, so it is not in the selections at all — but on a
        // player's machine it is an ordinary mod that has to take part in the sort. Put it back.
        if (Coop(prepared) is { } coop && !mods.Any(m => m.Id.Equals(coop.Id, StringComparison.OrdinalIgnoreCase)))
            mods.Add(coop);

        // Officials are not part of a shared list (see ModListFile.Mods), but the sorter needs them present or every
        // mod that depends on StoryMode counts as unsatisfiable. Client profile: nothing is pinned after the mods.
        var officials = prepared.Catalog.Modules
            .Where(m => OfficialModules.IsGameModule(m.Id) && !mods.Any(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        return LoadOrder.Compute(officials, mods, profile.Mods.Select(m => m.Id).ToList(), LoadOrder.Profile.Client,
            profile.ManualLoadOrder ? LoadOrder.OrderPolicy.Manual : LoadOrder.OrderPolicy.Suggest,
            knownToFollowCoop ?? CompatDb.Current.ClientFollowsCoop()).ModuleIds;
    }

    /// <summary>
    /// The Coop module as a player loads it. Host mode has it as server stock; a player has it from the workshop,
    /// where it is an ordinary selection and already in the list.
    /// </summary>
    public static DiscoveredModule? Coop(ModuleSelectionResult prepared) =>
        prepared.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop")
        ?? prepared.Catalog.Modules.FirstOrDefault(m => ClientManifest.CoopClientModuleIds.Contains(m.Id));
}
