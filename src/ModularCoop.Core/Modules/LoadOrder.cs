using Bannerlord.ModuleManager;

namespace ModularCoop.Core.Modules;

/// <summary>
/// Computes the engine load order. Stock modules keep the official host's order; community modules are placed by
/// BUTR's topological sort (DependedModules / DependedModuleMetadatas / ModulesToLoadAfterThis), before Coop.
/// </summary>
public static class LoadOrder
{
    /// <summary>Stock module FOLDER names in the official host's load order (ids are read from each SubModule.xml).</summary>
    public static readonly string[] Stock = ["Native", "SandBoxCore", "SandBox", "Coop", "DedicatedServer.Windows"];

    /// <summary>
    /// Official client modules that never exist on the dedicated server. Mods routinely declare them as hard
    /// dependencies (StoryMode above all); the server treats them as satisfied, like the official validator does
    /// (it exempts official non-DLC modules from matching).
    /// </summary>
    public static readonly string[] SoftOfficialDependencies = ["StoryMode", "CustomBattle", "BirthAndDeath", "Multiplayer", "FastMode"];

    public sealed record Result(IReadOnlyList<string> ModuleIds, IReadOnlyList<string> Issues);

    public static Result Compute(IReadOnlyList<DiscoveredModule> stock, IReadOnlyList<DiscoveredModule> community, IReadOnlyList<string>? preferredOrder = null)
    {
        var issues = new List<string>();
        var real = stock.Concat(community).Select(m => m.Info).ToList();

        // Phantom entries make the soft official dependencies "present" for the sorter and the validator.
        var present = new HashSet<string>(real.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var phantoms = SoftOfficialDependencies.Where(id => !present.Contains(id))
            .Select(id => new ModuleInfoExtended { Id = id, Name = id, IsOfficial = true, Version = stock.FirstOrDefault(s => s.Id == "Native")?.Info.Version ?? ApplicationVersion.Empty })
            .ToList();
        var all = real.Concat(phantoms).ToList();
        var phantomIds = new HashSet<string>(phantoms.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        IList<ModuleInfoExtended> sorted;
        try { sorted = ModuleSorter.Sort(all, new ModuleSorterOptions(skipOptionals: false, skipExternalDependencies: true)); }
        catch (Exception ex) { issues.Add("sorter failed: " + ex.Message); sorted = all; }

        var communityIds = new HashSet<string>(community.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var communitySorted = sorted.Where(m => communityIds.Contains(m.Id)).Select(m => m.Id).ToList();
        foreach (var c in community)
        {
            if (communitySorted.Contains(c.Id, StringComparer.OrdinalIgnoreCase)) continue;
            var missing = c.Info.DependentModules.Where(d => !d.IsOptional && !present.Contains(d.Id) && !phantomIds.Contains(d.Id)).Select(d => d.Id)
                .Concat(c.Info.DependentModuleMetadatas.Where(d => !d.IsOptional && !d.IsIncompatible && !present.Contains(d.Id) && !phantomIds.Contains(d.Id)).Select(d => d.Id))
                .Distinct().ToList();
            issues.Add($"{c.Id}: missing dependencies [{string.Join(", ", missing)}] — appended at the end of the community block");
            communitySorted.Add(c.Id);
        }

        // Honour a user-preferred order where it does not contradict a hard dependency.
        if (preferredOrder is { Count: > 0 })
        {
            var pref = preferredOrder.Where(id => communityIds.Contains(id)).ToList();
            var stable = communitySorted.OrderBy(id => { var i = pref.FindIndex(p => p.Equals(id, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }).ToList();
            if (!ViolatesDependencies(stable, community)) communitySorted = stable;
            else issues.Add("preferred order ignored: it would load a module before one it depends on");
        }

        // Whole-set topological order from BUTR (this is what lets frameworks that declare "load Native after me",
        // e.g. Harmony/ButterLib/UIExtenderEx/MCM, land before Native, exactly like the game launcher does), with the
        // community block re-arranged to the user's preference where dependencies allow. Then the two things the
        // official host pins: <Coop id> after everything else, DedicatedServer.Windows last. Stock modules are matched by
        // FOLDER name because the workshop build's Coop folder carries the id "CoopNightly"; the token needs the id.
        string? StockId(string folder) => stock.FirstOrDefault(m => m.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Id;
        var coopId = StockId("Coop");
        var dsId = StockId("DedicatedServer.Windows");
        var fullSorted = sorted.Select(m => m.Id).Where(id => !phantomIds.Contains(id)).ToList();
        foreach (var c in community) if (!fullSorted.Contains(c.Id, StringComparer.OrdinalIgnoreCase)) fullSorted.Add(c.Id);
        // Re-thread the community ids in the order decided above (sorter + preference) into the slots they occupy.
        var slots = fullSorted.Select((id, i) => (id, i)).Where(t => communityIds.Contains(t.id)).Select(t => t.i).ToList();
        for (int k = 0; k < slots.Count && k < communitySorted.Count; k++) fullSorted[slots[k]] = communitySorted[k];
        var ordered = fullSorted.Where(id => id != coopId && id != dsId).ToList();
        if (coopId is not null) ordered.Add(coopId);
        if (dsId is not null) ordered.Add(dsId);

        // Validate the FINAL order (phantoms appended last so they count as present without affecting the token).
        var byId = all.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var finalList = ordered.Select(id => byId[id]).Concat(phantoms).ToList();
        foreach (var m in finalList)
        {
            if (phantomIds.Contains(m.Id)) continue;
            foreach (var issue in ModuleUtilities.ValidateLoadOrder(finalList, m))
            {
                if (phantomIds.Contains(issue.SourceId)) continue;               // ordering relative to a phantom is meaningless
                if (issue.SourceId.Equals(dsId, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(dsId, StringComparison.OrdinalIgnoreCase)) continue; // DS.Windows is pinned last by the official host
                issues.Add($"{m.Id}: {issue.Reason}");
            }
        }
        return new Result(ordered, issues);
    }

    private static bool ViolatesDependencies(IReadOnlyList<string> order, IReadOnlyList<DiscoveredModule> community)
    {
        var index = order.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i, StringComparer.OrdinalIgnoreCase);
        foreach (var m in community)
        {
            var deps = m.Info.DependentModules.Select(d => d.Id)
                .Concat(m.Info.DependentModuleMetadatas.Where(d => d.LoadType == LoadType.LoadBeforeThis && !d.IsIncompatible).Select(d => d.Id));
            foreach (var dep in deps)
                if (index.TryGetValue(dep, out var di) && index.TryGetValue(m.Id, out var mi) && di > mi) return true;
            foreach (var after in m.Info.ModulesToLoadAfterThis.Select(d => d.Id))
                if (index.TryGetValue(after, out var ai) && index.TryGetValue(m.Id, out var mi2) && ai < mi2) return true;
        }
        return false;
    }
}
