using System.Xml;
using ModularCoop.Core.Modules;
using ModularCoop.Core.Profiles;
using ModularCoop.Core.Saves;

namespace ModularCoop.Core.Export;

/// <summary>
/// Brings the client's LauncherData.xml in line with the server's module plan, so a host on this PC can join their
/// own server without hand-ticking the list. Coop's ModuleValidator wants every community module the server runs
/// enabled on the client at the same version, and nothing extra.
///
/// Deliberately narrow: it only ever writes IsSelected and node order under SingleplayerData/ModDatas. The launcher's
/// MultiplayerData list and its DLLCheckData allow-list are left exactly as found, as are the official TaleWorlds
/// modules and the Coop module itself. It cannot install a missing mod or change a version on disk, so those are
/// reported rather than fixed.
/// </summary>
public static class LauncherDataSync
{
    public enum SyncAction
    {
        /// <summary>Tick a module the server runs.</summary>
        Enable,
        /// <summary>Untick a community module the server does not run (the validator rejects extras).</summary>
        Disable,
        /// <summary>Move a module so the client's load order matches the server's.</summary>
        Move,
        /// <summary>Nothing here can fix it: the mod is missing on this PC, or installed at a different version.</summary>
        Unfixable,
        /// <summary>Something ambiguous we refuse to guess at.</summary>
        Warning,
    }

    public sealed record PlannedChange(string Id, SyncAction Action, string Detail)
    {
        public override string ToString() => $"{Action,-9} {Id,-30} {Detail}";
    }

    /// <param name="Changes">Everything to show the player: the edits, plus what they have to fix themselves.</param>
    /// <param name="TargetOrder">
    /// The full sequence the reordered modules must end up in. The Move entries in <paramref name="Changes"/> only
    /// name the ones whose position actually changes, which is not enough to rebuild the list: an entry that keeps
    /// its index still has to keep its place relative to the ones that move around it.
    /// </param>
    public sealed record SyncPlan(IReadOnlyList<PlannedChange> Changes, IReadOnlyList<string> TargetOrder)
    {
        /// <summary>Edits we are going to make. A plan with none of these needs no dialog and writes nothing.</summary>
        public IReadOnlyList<PlannedChange> Edits =>
            Changes.Where(c => c.Action is SyncAction.Enable or SyncAction.Disable or SyncAction.Move).ToList();

        /// <summary>Things the player has to fix themselves. Always reported, whether or not there are edits.</summary>
        public IReadOnlyList<PlannedChange> Blockers =>
            Changes.Where(c => c.Action is SyncAction.Unfixable or SyncAction.Warning).ToList();

        public bool HasChanges => Changes.Any(c => c.Action is SyncAction.Enable or SyncAction.Disable or SyncAction.Move);
    }

    public sealed record SyncResult(string BackupPath, int Enabled, int Disabled, int Moved);

    /// <summary>Backups live next to the profiles, not in the game's Configs folder, which belongs to the game.</summary>
    public static string DefaultBackupRoot() => Path.Combine(ProfileStore.RootDir, "launcher-backups");

    private const string ModListPath = "/UserData/SingleplayerData/ModDatas/UserModData";

    private sealed record ClientEntry(XmlElement Element, string Id, string Version, bool Selected);

    /// <summary>
    /// Works out what would have to change for this client to satisfy the server, without touching anything.
    /// <paramref name="order"/> is the server's computed load order (LaunchSession.Prepared.Order).
    /// </summary>
    public static SyncPlan ComputePlan(IReadOnlyList<ClientManifest.Entry> serverMods, LoadOrder.Result order, string launcherDataPath)
    {
        var changes = new List<PlannedChange>();
        if (!File.Exists(launcherDataPath))
        {
            changes.Add(new PlannedChange(Path.GetFileName(launcherDataPath), SyncAction.Unfixable,
                "not found — run the Bannerlord launcher once so it creates " + launcherDataPath));
            return new SyncPlan(changes, []);
        }

        var doc = new XmlDocument();
        doc.Load(launcherDataPath);
        var client = ReadClientList(doc);

        // The Coop module: id depends on the build installed, so match both and never guess when both are present.
        var coop = client.Where(c => ClientManifest.CoopClientModuleIds.Contains(c.Id)).ToList();
        if (coop.Count > 1)
            changes.Add(new PlannedChange(string.Join(" + ", coop.Select(c => c.Id)), SyncAction.Warning,
                "two Coop modules are installed; leaving both alone — enable exactly one in the Bannerlord launcher"));
        else if (coop.Count == 0)
            changes.Add(new PlannedChange("Coop", SyncAction.Unfixable, "the Coop mod is not installed on this PC"));
        else if (!coop[0].Selected)
            changes.Add(new PlannedChange(coop[0].Id, SyncAction.Enable, "the Coop mod itself"));

        // What the server wants, minus everything the validator exempts and everything that only exists server-side.
        var wanted = serverMods
            .Where(e => !ClientManifest.OfficialModuleIds.Contains(e.Id)
                        && !ClientManifest.CoopClientModuleIds.Contains(e.Id)
                        && !ClientManifest.IsServerOnly(e.Id))
            .ToList();
        var wantedIds = new HashSet<string>(wanted.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var byId = client.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var enabling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in wanted)
        {
            if (!byId.TryGetValue(e.Id, out var c))
            {
                changes.Add(new PlannedChange(e.Id, SyncAction.Unfixable, $"the server runs {e.Version} but it is not installed on this PC"));
                continue;
            }
            // A version mismatch cannot be ticked away: the validator compares versions, so ticking the wrong one
            // would only look like it worked. Report it and leave the entry exactly as the player had it.
            if (!SaveHeaderReader.VersionsEqual(e.Version, c.Version))
            {
                changes.Add(new PlannedChange(e.Id, SyncAction.Unfixable, $"server has {e.Version}, this PC has {c.Version}"));
                continue;
            }
            if (!c.Selected) changes.Add(new PlannedChange(e.Id, SyncAction.Enable, "the server runs it"));
            enabling.Add(e.Id);
        }

        // Extras: anything else the player has ticked that the server is not running.
        foreach (var c in client)
        {
            if (!c.Selected) continue;
            if (ClientManifest.OfficialModuleIds.Contains(c.Id) || ClientManifest.CoopClientModuleIds.Contains(c.Id)) continue;
            if (wantedIds.Contains(c.Id)) continue;
            changes.Add(new PlannedChange(c.Id, SyncAction.Disable,
                c.Id.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase) ? "DLC must be off to join" : "the server does not run it"));
        }

        changes.AddRange(PlanOrder(client, order, enabling, out var targetOrder));
        return new SyncPlan(changes, targetOrder);
    }

    /// <summary>
    /// Moves only the modules that end up enabled and that the server also orders, into the server's relative order,
    /// in the positions they already occupy. Officials, the Coop entry and anything left unticked never move, so the
    /// player's own arrangement of mods they are not using now survives.
    /// </summary>
    private static List<PlannedChange> PlanOrder(IReadOnlyList<ClientEntry> client, LoadOrder.Result order,
                                                 IReadOnlySet<string> enabling, out IReadOnlyList<string> targetOrder)
    {
        var moves = new List<PlannedChange>();
        targetOrder = [];
        var target = order.ModuleIds
            .Where(id => !ClientManifest.OfficialModuleIds.Contains(id)
                         && !ClientManifest.CoopClientModuleIds.Contains(id)
                         && !ClientManifest.IsServerOnly(id)
                         && enabling.Contains(id))
            .ToList();

        // Their current relative order in the file, ignoring everything we are not moving.
        var current = client.Select(c => c.Id).Where(id => target.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (current.Count != target.Count || current.SequenceEqual(target, StringComparer.OrdinalIgnoreCase)) return moves;

        targetOrder = target;
        for (var i = 0; i < target.Count; i++)
        {
            var from = current.FindIndex(id => id.Equals(target[i], StringComparison.OrdinalIgnoreCase));
            if (from != i) moves.Add(new PlannedChange(target[i], SyncAction.Move, $"load order {from + 1} → {i + 1} of {target.Count}"));
        }
        return moves;
    }

    /// <summary>
    /// Applies exactly the plan that was shown. Backs the file up first, then rewrites it atomically. Returns null
    /// when there was nothing to do, in which case nothing is written and no backup is made.
    /// </summary>
    public static SyncResult? Apply(SyncPlan plan, string launcherDataPath, string backupRoot)
    {
        if (!plan.HasChanges || !File.Exists(launcherDataPath)) return null;

        var backupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, Path.GetFileName(launcherDataPath));
        File.Copy(launcherDataPath, backup, overwrite: true);

        // Mutate the document we loaded rather than regenerating one: everything we do not name — the XML
        // declaration, the xmlns attributes, MultiplayerData, DLLCheckData — is then untouched by construction.
        var doc = new XmlDocument();
        doc.Load(launcherDataPath);
        var client = ReadClientList(doc);
        var byId = client.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int enabled = 0, disabled = 0;
        foreach (var change in plan.Changes)
        {
            if (change.Action is not (SyncAction.Enable or SyncAction.Disable)) continue;
            if (!byId.TryGetValue(change.Id, out var entry)) continue;
            if (entry.Element.SelectSingleNode("IsSelected") is not XmlElement sel) continue;
            sel.InnerText = change.Action == SyncAction.Enable ? "true" : "false";
            if (change.Action == SyncAction.Enable) enabled++; else disabled++;
        }

        var moved = ApplyOrder(plan, client);

        var tmp = launcherDataPath + ".tmp";
        doc.Save(tmp);
        File.Move(tmp, launcherDataPath, overwrite: true);
        return new SyncResult(backup, enabled, disabled, moved);
    }

    /// <summary>Document order is the load order — there is no priority field — so this physically moves the nodes.</summary>
    private static int ApplyOrder(SyncPlan plan, IReadOnlyList<ClientEntry> client)
    {
        var target = plan.TargetOrder;
        var changed = plan.Changes.Count(c => c.Action == SyncAction.Move);
        if (target.Count == 0 || changed == 0) return 0;

        // Rebuild the whole reordered set in the target order, reusing the slots those entries already occupy so
        // nothing else in the file shifts. Entries that keep their index still take part: they anchor the ones
        // that move around them.
        var moving = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        var slots = client.Where(c => moving.Contains(c.Id)).Select(c => c.Element).ToList();
        var ordered = target.Select(id => client.First(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Element).ToList();
        if (slots.Count != ordered.Count) return 0;

        // Swap the entries between the slots they already occupy, so every OTHER node — officials, the Coop entry,
        // mods left unticked — keeps its exact position even when it sits between two that move. Placeholders hold
        // the slots while the elements are detached; each is replaced by the entry that belongs there.
        var parent = slots[0].ParentNode!;
        var placeholders = new List<XmlNode>();
        foreach (var el in slots)
        {
            var marker = parent.OwnerDocument!.CreateComment("");
            parent.InsertBefore(marker, el);
            parent.RemoveChild(el);
            placeholders.Add(marker);
        }
        for (var i = 0; i < ordered.Count; i++) parent.ReplaceChild(ordered[i], placeholders[i]);
        return changed;
    }

    private static List<ClientEntry> ReadClientList(XmlDocument doc)
    {
        var list = new List<ClientEntry>();
        foreach (XmlElement m in doc.SelectNodes(ModListPath)!)
        {
            var id = m.SelectSingleNode("Id")?.InnerText ?? "";
            if (id.Length == 0) continue;
            list.Add(new ClientEntry(m, id,
                m.SelectSingleNode("LastKnownVersion")?.InnerText ?? "",
                string.Equals(m.SelectSingleNode("IsSelected")?.InnerText, "true", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }
}
