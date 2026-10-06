using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Coop.Launch;

/// <summary>
/// The player's game, set up to join a server this app runs: the server's mods (minus the server-only ones) in the
/// profile's order, with Coop where the profile puts it. Host mode's Launch client and the smoke test both start this.
/// </summary>
public static class ServerMatchedClient
{
    public static ClientLaunchSession.Prepared Prepare(LaunchSession.Prepared target, Profile clientProfile)
    {
        var client = ProfileStore.Snapshot(clientProfile);
        client.Mods = target.Selections.Where(s => !ClientManifest.IsServerOnly(s.Module.Id))
            .Select(s => ClientEntry(s.Module, target.Catalog, clientProfile)).ToList();
        foreach (var missing in clientProfile.EnabledMods.Where(pm => !client.Mods.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)) &&
                     !ClientManifest.IsServerOnly(pm.Id) && !ClientManifest.CoopClientModuleIds.Contains(pm.Id)))
            client.Mods.Add(missing);

        // The selections come out in the SERVER's order, which says nothing about where these mods go on a player's
        // machine, so put them back in the profile's order first.
        var profileAt = clientProfile.Mods.Select((m, i) => (m.Id, i))
            .ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        client.Mods = client.Mods
            .OrderBy(m => profileAt.TryGetValue(m.Id, out var i) ? i : int.MaxValue)
            .ToList();

        // Coop then goes where the profile's Coop row puts it, NOT on the end. Appending it here is how a host who
        // launches the client from the Server panel handed their own game the server's arrangement -- Coop after
        // every mod -- which is the order that crashes anything binding to Coop's assemblies as it loads.
        var coop = target.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
        if (coop is not null)
            client.Mods.Insert(CoopInsertIndex(client.Mods, clientProfile.Mods, coop.Id),
                               new ProfileMod { Id = coop.Id, LastVersion = coop.Version });
        var result = ClientLaunchSession.Prepare(client);
        var mismatched = client.Mods.Where(pm => pm.LastVersion is not null && result.Mods.Any(m =>
            m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase) && !SaveHeaderReader.VersionsEqual(m.Version, pm.LastVersion))).Select(pm => pm.Id).ToList();
        if (mismatched.Count > 0) throw new InvalidOperationException("Client versions differ from the server: " + string.Join(", ", mismatched) + ". Update matching copies or restart the server.");
        return result;
    }

    /// <summary>
    /// The client's entry for one mod the server selected. Almost everything on a profile entry is about the server
    /// and is left behind, but the "server only" folders are the one thing that is about the player's game: without
    /// them a host's own client would be shown folders the profile says it must not see.
    /// </summary>
    public static ProfileMod ClientEntry(DiscoveredModule serverCopy, ModuleCatalog catalog, Profile hostProfile) => new()
    {
        Id = serverCopy.Id, Enabled = true, SourcePath = ClientCopy(serverCopy, catalog).FolderPath, LastVersion = serverCopy.Version,
        ClientExcludedFolders = hostProfile.Mods
            .FirstOrDefault(m => m.Id.Equals(serverCopy.Id, StringComparison.OrdinalIgnoreCase))?.ClientExcludedFolders?.ToList(),
    };

    /// <summary>
    /// The folder the player's game should load for a mod the server selected. Normally the very same one. The
    /// exception is a host who pins a copy cut down for the server (the same mod without <c>RuntimeDataCache</c>,
    /// say) while the full copy sits in the game's Modules folder: handing the game the server's folder would run
    /// the player on the stripped copy. The profile has one pin per mod and it is the server's, so the full copy is
    /// recognised from the outside instead - same id, same version, every top-level folder the server's copy has
    /// and more. Coop's handshake matches on id and version, so the two sides still agree.
    /// </summary>
    public static DiscoveredModule ClientCopy(DiscoveredModule serverCopy, ModuleCatalog catalog) =>
        ModuleCopies.FullerCopy(serverCopy, catalog.Candidates(serverCopy.Id)) ?? serverCopy;

    /// <summary>
    /// Where Coop belongs in a synthesized client list: after however many of these mods the profile places ahead of
    /// its own Coop row. Falls back to the end only when the profile never mentions Coop, which is the best guess
    /// available and matches what the dedicated server does.
    /// </summary>
    public static int CoopInsertIndex(IReadOnlyList<ProfileMod> mods, IReadOnlyList<ProfileMod> profile, string coopId)
    {
        var coopAt = profile.ToList().FindIndex(m => m.Id.Equals(coopId, StringComparison.OrdinalIgnoreCase)
                                                  || ClientManifest.CoopClientModuleIds.Contains(m.Id));
        if (coopAt < 0) return mods.Count;
        var ahead = new HashSet<string>(profile.Take(coopAt).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        return mods.Count(m => ahead.Contains(m.Id));
    }
}
