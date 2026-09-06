using Bannerlord.ModuleManager;

namespace ModderLords.Core.Modules;

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

    /// <summary>
    /// The parts of the order that differ between the two engines we launch. The dedicated server pins Coop and
    /// DedicatedServer.Windows at the end and has to invent the client-only official modules, because they are not
    /// installed there at all. On the player's own machine every official module is real and nothing is pinned
    /// after the mods, so both of those lists are empty.
    /// </summary>
    public sealed record Profile(IReadOnlyList<string> HeadFolders, IReadOnlyList<string> TailFolders, IReadOnlyList<string> PhantomDependencies)
    {
        /// <summary>The official host's order: Native, SandBoxCore, SandBox, community, Coop, DedicatedServer.Windows.</summary>
        public static readonly Profile DedicatedServer = new(["Native", "SandBoxCore", "SandBox"], ["Coop", "DedicatedServer.Windows"], SoftOfficialDependencies);

        /// <summary>The player's game: officials first, then mods. StoryMode and friends are installed, so they are
        /// ordered like any other module rather than faked.</summary>
        public static readonly Profile Client = new(["Native", "SandBoxCore", "SandBox"], [], []);
    }

    /// <summary>
    /// Whether a community module loads BEFORE the game's own modules, because its manifest says Native must load
    /// after it (<c>ModulesToLoadAfterThis</c>, or a <c>LoadAfterThis</c> metadata entry). Harmony, ButterLib,
    /// UIExtenderEx and MCM all do; the game launcher places them the same way.
    ///
    /// This is a fact about the mod, not a preference: <see cref="Compute"/> reads it, and the mod list in the app
    /// shows it, so the two can never disagree about where a framework sits.
    /// </summary>
    public static bool LoadsBeforeNative(DiscoveredModule module) =>
        module.Info.ModulesToLoadAfterThis.Any(d => d.Id.Equals("Native", StringComparison.OrdinalIgnoreCase))
        || module.Info.DependentModuleMetadatas.Any(d => d.LoadType == LoadType.LoadAfterThis && d.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));

    public sealed record Result(IReadOnlyList<string> ModuleIds, IReadOnlyList<string> Issues);

    public static Result Compute(IReadOnlyList<DiscoveredModule> stock, IReadOnlyList<DiscoveredModule> community, IReadOnlyList<string>? preferredOrder = null, Profile? profile = null)
    {
        profile ??= Profile.DedicatedServer;
        var issues = new List<string>();
        var real = stock.Concat(community).Select(m => m.Info).ToList();

        // Phantom entries make the soft official dependencies "present" for the sorter and the validator.
        var present = new HashSet<string>(real.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var phantoms = profile.PhantomDependencies.Where(id => !present.Contains(id))
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

        // Honour the user's order as far as the dependencies allow. It used to be all or nothing: one bad pair
        // discarded the whole preference, so moving any mod appeared to do nothing and the list disagreed with the
        // engine order with no way to reconcile them. Now only the mods that must move are moved.
        if (preferredOrder is { Count: > 0 })
        {
            var pref = preferredOrder.Where(id => communityIds.Contains(id)).ToList();
            var wanted = communitySorted.OrderBy(id => { var i = pref.FindIndex(p => p.Equals(id, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }).ToList();
            communitySorted = SortRespectingPreference(wanted, community, communitySorted);
            foreach (var conflict in DependencyConflicts(wanted, community))
                issues.Add("moved to satisfy a dependency: " + conflict);
        }

        // Whole-set topological order from BUTR (this is what lets frameworks that declare "load Native after me",
        // e.g. Harmony/ButterLib/UIExtenderEx/MCM, land before Native, exactly like the game launcher does), with the
        // community block re-arranged to the user's preference where dependencies allow. Then the two things the
        // official host pins: <Coop id> after everything else, DedicatedServer.Windows last. Stock modules are matched by
        // FOLDER name because the workshop build's Coop folder carries the id "CoopNightly"; the token needs the id.
        string? StockId(string folder) => stock.FirstOrDefault(m => m.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Id;
        var tailIds = profile.TailFolders.Select(StockId).Where(id => id is not null).Select(id => id!).ToList();
        var dsId = tailIds.Count > 0 ? tailIds[^1] : null;
        // A community module that declares "load Native after me" (ModulesToLoadAfterThis / LoadAfterThis metadata) belongs
        // in front of Native, as the game launcher places Harmony, ButterLib, UIExtenderEx and MCM. Everything else follows Sandbox.
        bool WantsToPrecedeNative(string id)
        {
            var m = community.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return m is not null && LoadsBeforeNative(m);
        }
        var ordered = new List<string>();
        ordered.AddRange(communitySorted.Where(WantsToPrecedeNative));
        var headIds = profile.HeadFolders.Select(StockId).Where(id => id is not null).Select(id => id!).ToList();
        ordered.AddRange(headIds);
        // Officials that are neither pinned at the front nor at the end -- StoryMode, CustomBattle and the rest on
        // the player's machine. They go straight after the head, in the sorter's order, because mods depend on them
        // and never the other way round. On the dedicated server there are none: those modules are phantoms there.
        var pinned = new HashSet<string>(headIds.Concat(tailIds), StringComparer.OrdinalIgnoreCase);
        var stockIds = new HashSet<string>(stock.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(sorted.Select(m => m.Id).Where(id => stockIds.Contains(id) && !pinned.Contains(id) && !phantomIds.Contains(id)));
        ordered.AddRange(communitySorted.Where(id => !WantsToPrecedeNative(id)));
        ordered.AddRange(tailIds);

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

    /// <summary>
    /// Orders the community block by dependency, breaking every free choice in favour of the order the user asked
    /// for. A mod only moves when something it needs would otherwise load after it, so an unrelated mod dragged up
    /// or down keeps its new place.
    /// </summary>
    private static List<string> SortRespectingPreference(IReadOnlyList<string> wanted, IReadOnlyList<DiscoveredModule> community, IReadOnlyList<string> fallback)
    {
        var rank = wanted.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        var before = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // id -> what must load before it
        foreach (var id in wanted) before[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in community)
        {
            if (!before.ContainsKey(m.Id)) continue;
            var deps = m.Info.DependentModules.Select(d => d.Id)
                .Concat(m.Info.DependentModuleMetadatas.Where(d => d.LoadType == LoadType.LoadBeforeThis && !d.IsIncompatible).Select(d => d.Id));
            foreach (var dep in deps)
                if (before.ContainsKey(dep)) before[m.Id].Add(dep);
            foreach (var after in m.Info.ModulesToLoadAfterThis.Select(d => d.Id))
                if (before.ContainsKey(after)) before[after].Add(m.Id);
        }

        var result = new List<string>(wanted.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<string>(wanted);
        while (remaining.Count > 0)
        {
            // Of everything whose dependencies are already placed, take whichever the user put first.
            var next = remaining.Where(id => before[id].All(placed.Contains))
                                .OrderBy(id => rank.TryGetValue(id, out var r) ? r : int.MaxValue)
                                .FirstOrDefault();
            if (next is null) return fallback.ToList();   // a cycle: leave it to the sorter that already coped
            result.Add(next);
            placed.Add(next);
            remaining.Remove(next);
        }
        return result;
    }

    /// <summary>
    /// Every place the requested order would load something before what it needs, described well enough to act on:
    /// which mod, which dependency, and which way round they have to go.
    /// </summary>
    private static List<string> DependencyConflicts(IReadOnlyList<string> order, IReadOnlyList<DiscoveredModule> community)
    {
        var conflicts = new List<string>();
        var index = order.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i, StringComparer.OrdinalIgnoreCase);
        foreach (var m in community)
        {
            var deps = m.Info.DependentModules.Select(d => d.Id)
                .Concat(m.Info.DependentModuleMetadatas.Where(d => d.LoadType == LoadType.LoadBeforeThis && !d.IsIncompatible).Select(d => d.Id));
            foreach (var dep in deps)
                if (index.TryGetValue(dep, out var di) && index.TryGetValue(m.Id, out var mi) && di > mi)
                    conflicts.Add($"{m.Id} needs {dep} loaded first, but you put {dep} after it");
            foreach (var after in m.Info.ModulesToLoadAfterThis.Select(d => d.Id))
                if (index.TryGetValue(after, out var ai) && index.TryGetValue(m.Id, out var mi2) && ai < mi2)
                    conflicts.Add($"{m.Id} declares that {after} loads after it, but you put {after} first");
        }
        // The same pair can be declared both ways round in a manifest; say it once.
        return conflicts.Distinct().ToList();
    }
}
