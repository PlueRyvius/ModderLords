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
    /// <summary>
    /// Whether a community module has to load AFTER Coop, because its manifest says Coop loads before it.
    ///
    /// The official host pins Coop after every community module, which is right for mods that know nothing about
    /// it. A compatibility module that patches Coop itself is the opposite case: TAOM's own TAOM.CoopCompat
    /// declares <c>&lt;DependedModuleMetadata id="CoopNightly" order="LoadBeforeThis" /&gt;</c> and its bootstrap
    /// submodule binds to Coop's assemblies as it loads, so placing it ahead of Coop puts it in front of the thing
    /// it exists to patch. It still goes before DedicatedServer.Windows, which the host pins absolutely last.
    /// </summary>
    /// <param name="knownToFollowCoop">
    /// Mods curated as patching Coop even though their manifest is silent about it. CoopMarriage and CoopModPatch
    /// declare nothing at all about Coop, so metadata alone cannot place them and loading them first crashes the
    /// game at startup. Supplied by the compatibility database; see <c>CompatDb.ClientFollowsCoop</c>.
    /// </param>
    public static bool LoadsAfterCoop(DiscoveredModule module, IReadOnlyCollection<string> coopIds,
                                      IReadOnlyCollection<string>? knownToFollowCoop = null) =>
        knownToFollowCoop is not null && knownToFollowCoop.Contains(module.Id, StringComparer.OrdinalIgnoreCase)
        || module.Info.DependentModuleMetadatas.Any(d => d.LoadType == LoadType.LoadBeforeThis &&
            coopIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase));

    public static bool LoadsBeforeNative(DiscoveredModule module) =>
        module.Info.ModulesToLoadAfterThis.Any(d => d.Id.Equals("Native", StringComparison.OrdinalIgnoreCase))
        || module.Info.DependentModuleMetadatas.Any(d => d.LoadType == LoadType.LoadAfterThis && d.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));

    public sealed record Result(IReadOnlyList<string> ModuleIds, IReadOnlyList<string> Issues);

    /// <summary>
    /// How much authority the computed order has over the user's. The order this class produces is assembled from
    /// module metadata, and metadata is sometimes simply wrong -- TAOM_Map declares that TAOM loads before it, which
    /// contradicts the load order TAOM's own authors publish. Dependency logic cannot cover every such case, so the
    /// user must be able to overrule it; what does not change is that the conflicts are still reported either way.
    /// </summary>
    public enum OrderPolicy
    {
        /// <summary>Move mods when a manifest says they must move, and say so. The default.</summary>
        Suggest,
        /// <summary>Take the requested order verbatim. Conflicts become warnings instead of moves.</summary>
        Manual,
    }

    public static Result Compute(IReadOnlyList<DiscoveredModule> stock, IReadOnlyList<DiscoveredModule> community, IReadOnlyList<string>? preferredOrder = null, Profile? profile = null, OrderPolicy policy = OrderPolicy.Suggest, IReadOnlyCollection<string>? knownToFollowCoop = null)
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
            // Anything the preference does not mention keeps the sorter's relative position, at the end.
            var wanted = communitySorted.OrderBy(id => { var i = pref.FindIndex(p => p.Equals(id, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }).ToList();
            var conflicts = DependencyConflicts(wanted, community);
            if (policy == OrderPolicy.Manual)
            {
                // The user's order stands. Still say exactly what a manifest disagrees with, so an override is an
                // informed one rather than a silent one.
                communitySorted = wanted;
                foreach (var conflict in conflicts)
                    issues.Add("kept your order despite a declared dependency: " + conflict);
            }
            else
            {
                communitySorted = SortRespectingPreference(wanted, community, communitySorted);
                foreach (var conflict in conflicts)
                    issues.Add("moved to satisfy a dependency: " + conflict);
            }
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

        // A module that patches Coop has to follow it. Everything else keeps the host's order, where Coop trails
        // the community block and DedicatedServer.Windows is last of all.
        var coopIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Coop", "CoopNightly" };
        if (StockId("Coop") is { } resolvedCoopId) coopIds.Add(resolvedCoopId);
        bool WantsToFollowCoop(string id)
        {
            var m = community.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return m is not null && LoadsAfterCoop(m, coopIds, knownToFollowCoop);
        }
        // Following Coop only means something when Coop is actually in the order. On the dedicated server it always
        // is, pinned in the tail. On a player's machine it is an ordinary community module -- possibly absent
        // entirely, for someone using this purely as a mod loader, and then these mods have nothing to follow and
        // are placed like any other.
        var coopInCommunity = communitySorted.FirstOrDefault(id => coopIds.Contains(id));
        var coopIsOrdered = tailIds.Count > 0 || coopInCommunity is not null;
        var wantsToFollow = coopIsOrdered
            ? communitySorted.Where(id => !WantsToPrecedeNative(id) && !coopIds.Contains(id) && WantsToFollowCoop(id)).ToList()
            : [];

        // On the server Coop trails the whole community block, so every follower moves and the move is structural,
        // not a correction of anything the user chose -- it is not worth saying. On the player's machine the order
        // is theirs, so a move is worth reporting, and under "My order wins" it is not made at all.
        var coopName = coopInCommunity ?? (tailIds.Count > 0 ? tailIds[0] : "Coop");
        var followCoop = policy == OrderPolicy.Manual && tailIds.Count == 0 ? [] : wantsToFollow;
        var followCoopSet = new HashSet<string>(followCoop, StringComparer.OrdinalIgnoreCase);

        if (tailIds.Count == 0)
            foreach (var id in wantsToFollow)
            {
                // Only the ones actually out of place: a mod the user already put after Coop needs no comment.
                if (communitySorted.IndexOf(id) > communitySorted.IndexOf(coopName)) continue;
                var fromDb = knownToFollowCoop is not null && knownToFollowCoop.Contains(id, StringComparer.OrdinalIgnoreCase);
                var source = fromDb ? " (from the compatibility database; its own manifest does not say so)" : "";
                issues.Add(policy == OrderPolicy.Manual
                    ? $"{id}: kept your order — but it patches {coopName} and is set to load BEFORE it. Bannerlord will crash at startup. Move it below {coopName}, or untick My order wins.{source}"
                    : $"{id}: moved after {coopName} — it patches Coop and crashes the game at startup if it loads first.{source}");
            }

        ordered.AddRange(communitySorted.Where(id => !WantsToPrecedeNative(id) && !followCoopSet.Contains(id)));
        if (tailIds.Count == 0)
        {
            // The player's own game. Coop sits wherever the user put it, so the mods that patch it go immediately
            // after it rather than at the end of the list -- appending them only lands after Coop by accident, and
            // not at all when Coop itself is last, which is exactly the order that crashes the game at startup.
            if (coopInCommunity is not null && followCoop.Count > 0)
                ordered.InsertRange(ordered.IndexOf(coopInCommunity) + 1, followCoop);
        }
        else
        {
            ordered.AddRange(tailIds.Take(tailIds.Count - 1));   // Coop
            ordered.AddRange(followCoop);                        // the modules that patch it
            ordered.Add(tailIds[^1]);                            // DedicatedServer.Windows, always last
        }

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
