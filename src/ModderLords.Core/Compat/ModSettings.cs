using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Compat;

/// <summary>
/// What a profile says about one mod, in the terms a compat record uses: the Role column, the Server-only logic tick
/// and the two folder lists. These are the settings a host changes on the Mods tab, and they live in the profile, not
/// in the compat database - which is why Submit… has to fetch them from here: a record on its own carries none of
/// them unless somebody copied them in by hand.
///
/// The folder lists are the effective ones (the profile's, or the compat record's where the profile has no opinion),
/// trimmed, without repeats and sorted, so two values that mean the same launch compare equal.
/// </summary>
public sealed record ModSettings(
    ServerRole Role,
    bool ServerAuthoritative,
    IReadOnlyList<string> ClientSideBehaviors,
    IReadOnlyList<string> ServerExcludedFolders,
    IReadOnlyList<string> ClientExcludedFolders)
{
    public static ModSettings From(ProfileMod mod, CompatRecord? record) => new(
        mod.Role,
        mod.ServerAuthoritative,
        // The list only means something while the tick is on; kept out otherwise so toggling the tick off and on
        // around an untouched list is not a different setting.
        mod.ServerAuthoritative ? mod.ClientSideBehaviors.Distinct(StringComparer.Ordinal).OrderBy(b => b, StringComparer.Ordinal).ToList() : [],
        Folders(mod.ServerExcludedFolders ?? record?.ServerExcludedFolders),
        Folders(mod.ClientExcludedFolders ?? record?.ClientExcludedFolders));

    /// <summary>Names a launch would honour: what it ignores with a warning has no place in a record sent to everyone.</summary>
    private static List<string> Folders(IEnumerable<string>? names) =>
        ServerFolderExclusions.Split(names).Valid.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public bool Same(ModSettings? other) =>
        other is not null && Role == other.Role && ServerAuthoritative == other.ServerAuthoritative
        && ClientSideBehaviors.SequenceEqual(other.ClientSideBehaviors, StringComparer.Ordinal)
        && ServerExcludedFolders.SequenceEqual(other.ServerExcludedFolders, StringComparer.OrdinalIgnoreCase)
        && ClientExcludedFolders.SequenceEqual(other.ClientExcludedFolders, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The record to submit: <paramref name="record"/> (the effective one, or null when the mod has none) with these
    /// settings written into the fields that hold them. Everything else in the record is kept as it is.
    /// </summary>
    public CompatRecord Over(string id, CompatRecord? record)
    {
        var r = record?.Clone() ?? new CompatRecord { Id = id };
        // Left unset while the role is the one a new profile would get anyway, so a mod nobody changed does not
        // gain a field that says nothing.
        var roleWithoutRecord = CompatDb.FallbackRoles.TryGetValue(r.Id, out var fallback) ? fallback : ServerRole.Run;
        if (Role != (r.DefaultRole ?? roleWithoutRecord)) r.DefaultRole = Role;
        if (ServerAuthoritative) (r.ServerAuthoritative, r.ClientSideBehaviors) = (true, ClientSideBehaviors.ToList());
        else if (r.ServerAuthoritative is not null) r.ServerAuthoritative = false;
        r.ServerExcludedFolders = ServerExcludedFolders.ToList();
        r.ClientExcludedFolders = ClientExcludedFolders.ToList();
        return r;
    }

    /// <summary>The settings in the Mods tab's words, for the question Submit… asks before opening the browser.</summary>
    public string Describe() =>
        $"role {RoleLabels.Describe(Role, ServerAuthoritative, "(unset)")}"
        + $"; client-only folders: {(ServerExcludedFolders.Count > 0 ? string.Join(", ", ServerExcludedFolders) : "none")}"
        + $"; server-only folders: {(ClientExcludedFolders.Count > 0 ? string.Join(", ", ClientExcludedFolders) : "none")}";
}
