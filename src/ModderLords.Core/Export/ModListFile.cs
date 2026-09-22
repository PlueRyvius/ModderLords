using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Export;

/// <summary>
/// A shareable mod list: which modules, at which versions, in which load order, plus the server-side roles a host
/// needs. One file covers both people it might be handed to — a player who wants their Bannerlord launcher set up
/// to join, and another host who wants to run the same server.
///
/// The order of <see cref="Mods"/> is the load order; there is no separate order field to fall out of step with it.
/// </summary>
public sealed record ModListFile
{
    /// <summary>
    /// Bumped only when an older launcher could not read the file; unknown newer versions are refused.
    ///
    /// Format 2 records where Coop loads: it is an ordinary entry in <see cref="Mods"/>, at its position on a
    /// PLAYER's machine, and the order is computed the same way whether a host or a player exported it. Format 1
    /// left Coop out and carried whatever order the exporting mode's engine used, so a host's list told importers
    /// "Coop last" — the server's answer, and the one that crashes a client running mods that patch Coop.
    /// </summary>
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string? ExportedBy { get; init; }
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Free text from the host, e.g. the profile name, shown to whoever imports it.</summary>
    public string? Name { get; init; }
    /// <summary>The Coop build the host is running, so an importer can see at a glance whether theirs matches.</summary>
    public CoopRef? Coop { get; init; }
    /// <summary>
    /// The mods, in the order they load on a player's machine. From format 2 the Coop module is one of them, at its
    /// client position; <see cref="Coop"/> still records the host's build for the version check.
    /// </summary>
    public IReadOnlyList<ModEntry> Mods { get; init; } = [];
    // Null means an older export did not specify the official selection.
    public IReadOnlyList<string>? ClientOfficialModules { get; init; }

    public sealed record CoopRef(string Id, string Version);

    /// <param name="Source">Workshop link where we know one, so a missing mod can actually be found.</param>
    public sealed record ModEntry(string Id, string Version, string? Source, ServerRole Role, bool ServerAuthoritative);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds the file from a chosen mod set: the selections give the mods and roles, the order gives the order.</summary>
    public static ModListFile From(ModuleSelectionResult prepared, Profile profile, string? exportedBy = null)
    {
        // The CLIENT order, not the order of whichever game this profile was prepared to launch. A host and a player
        // exporting the same profile must hand out the same file; see ClientOrder.
        var position = ClientOrder.For(prepared, profile)
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        var authoritative = profile.Mods
            .Where(m => m.ServerAuthoritative)
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Roles come from the profile, not from the selections: a player's prepare has no roles to report (every mod
        // simply loads), so reading them from there would make a player's export differ from a host's.
        var roles = profile.Mods.ToDictionary(m => m.Id, m => m.Role, StringComparer.OrdinalIgnoreCase);
        // A link set on the profile wins over one read from the folder name: it is how a mod installed by hand into
        // Modules, which has no Workshop folder to read, still reaches the importer with somewhere to get it.
        var links = profile.Mods.Where(m => !string.IsNullOrWhiteSpace(m.DownloadUrl))
            .ToDictionary(m => m.Id, m => m.DownloadUrl!, StringComparer.OrdinalIgnoreCase);
        ServerRole RoleOf(string id) => roles.TryGetValue(id, out var r) ? r : ServerRole.AsShipped;

        var coop = ClientOrder.Coop(prepared);
        var exported = prepared.Selections.Select(s => s.Module)
            .Where(m => !ClientManifest.IsServerOnly(m.Id))   // the launcher's own server module means nothing to anyone else
            .ToList();
        // Host mode keeps Coop as server stock, so it is not in the selections — but its client position is the
        // thing this file exists to carry, so it is an entry like any other.
        if (coop is not null && !exported.Any(m => ClientManifest.CoopClientModuleIds.Contains(m.Id)))
            exported.Add(coop);

        var mods = exported
            .OrderBy(m => position.TryGetValue(m.Id, out var i) ? i : int.MaxValue)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => new ModEntry(m.Id, m.Version, links.GetValueOrDefault(m.Id) ?? ClientManifest.WorkshopUrl(m.FolderPath),
                                      RoleOf(m.Id), authoritative.Contains(m.Id)))
            .ToList();

        var installedCount = mods.Count;
        foreach (var missing in profile.EnabledMods.Where(pm => !mods.Any(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase)) &&
                     !ClientManifest.IsServerOnly(pm.Id) && !OfficialModules.IsGameModule(pm.Id)))
            mods.Add(new ModEntry(missing.Id, missing.LastVersion ?? "", missing.DownloadUrl, missing.Role, missing.ServerAuthoritative));
        // Until every requested module is installed there is no complete engine order to export.
        // Preserve the requested order, rather than moving missing requirements to the end on every round-trip.
        if (mods.Count != installedCount)
            mods = mods.OrderBy(m =>
            {
                var index = profile.Mods.FindIndex(pm => pm.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            }).ToList();
        return new ModListFile
        {
            ExportedBy = exportedBy,
            Name = profile.Name,
            Coop = coop is null ? null : new CoopRef(coop.Id, coop.Version),
            Mods = mods,
            ClientOfficialModules = profile.ClientOfficialModules.ToList(),
        };
    }

    /// <summary>
    /// The list a profile holds, exactly as written: its enabled mods in its order, installed or not. The inverse of
    /// <see cref="ToProfile"/>, used to re-apply an imported list to the launcher once its missing mods are downloaded.
    /// Unlike <see cref="From"/> it needs no scan, so the entries still missing on this PC keep their place.
    /// </summary>
    public static ModListFile AsListed(Profile profile) => new()
    {
        Name = profile.Name,
        Mods = profile.EnabledMods
            .Where(m => !ClientManifest.IsServerOnly(m.Id) && !OfficialModules.IsGameModule(m.Id))
            .Select(m => new ModEntry(m.Id, m.LastVersion ?? "", m.DownloadUrl, m.Role, m.ServerAuthoritative))
            .ToList(),
        ClientOfficialModules = profile.ClientOfficialModules.ToList(),
    };

    /// <summary>Ids in the list (Coop aside) that are not among <paramref name="installed"/>.</summary>
    public IReadOnlyList<string> NotInstalled(IReadOnlySet<string> installed) =>
        ToClientEntries().Select(e => e.Id).Where(id => !installed.Contains(id)).ToList();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static void Write(string path, ModListFile file)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, file.ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a shared list. Throws with a readable message rather than a raw JSON error.</summary>
    public static ModListFile Read(string path)
    {
        ModListFile? file;
        try { file = JsonSerializer.Deserialize<ModListFile>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidOperationException($"{Path.GetFileName(path)} is not a mod list this launcher can read: {ex.Message}"); }

        if (file is null) throw new InvalidOperationException($"{Path.GetFileName(path)} is empty");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidOperationException($"{Path.GetFileName(path)} was written by a newer launcher (format {file.FormatVersion}); update this one to read it");
        if (file.Mods.Count == 0 && file.ClientOfficialModules is null) throw new InvalidOperationException($"{Path.GetFileName(path)} lists no mods");
        return file;
    }

    /// <summary>
    /// A format 1 list, which never recorded where Coop loads. Importers place it last (what the file meant at the
    /// time) and should say so: last is wrong for anyone running mods that patch Coop.
    /// </summary>
    public bool CoopPositionUnknown => FormatVersion < 2 && Coop is not null
        && !Mods.Any(m => ClientManifest.CoopClientModuleIds.Contains(m.Id));

    /// <summary>
    /// The list as the launcher-data sync wants it. Coop is left out: the sync enables it and never version-checks
    /// it, so an entry here would only produce a version mismatch it cannot act on.
    /// </summary>
    public IReadOnlyList<ClientManifest.Entry> ToClientEntries() =>
        Mods.Where(m => !ClientManifest.CoopClientModuleIds.Contains(m.Id))
            .Select(m => new ClientManifest.Entry(m.Id, m.Version, m.Source)).ToList();

    /// <summary>The shared CLIENT load order, in the shape the sync expects. From format 2 Coop is in it.</summary>
    public LoadOrder.Result ToOrder() => new(Mods.Select(m => m.Id).ToList(), []);

    /// <summary>
    /// A profile another host can run: same mods, same roles, same order. Copies are resolved on this PC by id, so
    /// the exporter's folder layout does not have to be reproduced.
    ///
    /// From format 2 Coop is an ordinary entry and lands at the position the file gives it. A format 1 file never
    /// recorded one, so it goes last — see <see cref="CoopPositionUnknown"/>, which importers surface.
    /// </summary>
    public Profile ToProfile(string name) => new()
    {
        Name = name,
        ClientOfficialModules = ClientOfficialModules?.ToList() ?? new Profile().ClientOfficialModules,
        Mods = Mods.Select(m => new ProfileMod
        {
            Id = m.Id,
            Role = m.Role,
            Enabled = true,
            ServerAuthoritative = m.ServerAuthoritative,
            LastVersion = m.Version,
            DownloadUrl = m.Source,
        }).Concat(Coop is not null && !Mods.Any(m => ClientManifest.CoopClientModuleIds.Contains(m.Id))
            ? new[] { new ProfileMod { Id = Coop.Id, LastVersion = Coop.Version, Enabled = true } }
            : Array.Empty<ProfileMod>()).ToList(),
    };
}
