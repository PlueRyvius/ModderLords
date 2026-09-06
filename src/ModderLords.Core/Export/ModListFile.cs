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
    /// <summary>Bumped only when an older launcher could not read the file; unknown newer versions are refused.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string? ExportedBy { get; init; }
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Free text from the host, e.g. the profile name, shown to whoever imports it.</summary>
    public string? Name { get; init; }
    /// <summary>The Coop build the host is running, so an importer can see at a glance whether theirs matches.</summary>
    public CoopRef? Coop { get; init; }
    public IReadOnlyList<ModEntry> Mods { get; init; } = [];

    public sealed record CoopRef(string Id, string Version);

    /// <param name="Source">Workshop link where we know one, so a missing mod can actually be found.</param>
    public sealed record ModEntry(string Id, string Version, string? Source, ServerRole Role, bool ServerAuthoritative);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds the file from a prepared launch: the selections give the mods and roles, the plan gives the order.</summary>
    public static ModListFile From(LaunchSession.Prepared prepared, Profile profile, string? exportedBy = null)
    {
        var position = prepared.Order.ModuleIds
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        var authoritative = profile.Mods
            .Where(m => m.ServerAuthoritative)
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mods = prepared.Selections
            .Where(s => !ClientManifest.IsServerOnly(s.Module.Id))   // the launcher's own server module means nothing to anyone else
            .OrderBy(s => position.TryGetValue(s.Module.Id, out var i) ? i : int.MaxValue)
            .ThenBy(s => s.Module.Id, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ModEntry(s.Module.Id, s.Module.Version, ClientManifest.WorkshopUrl(s.Module.FolderPath),
                                      s.Role, authoritative.Contains(s.Module.Id)))
            .ToList();

        var coop = prepared.Catalog.Modules.FirstOrDefault(m => m.IsStock && m.FolderName == "Coop");
        return new ModListFile
        {
            ExportedBy = exportedBy,
            Name = profile.Name,
            Coop = coop is null ? null : new CoopRef(coop.Id, coop.Version),
            Mods = mods,
        };
    }

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
        if (file.Mods.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(path)} lists no mods");
        return file;
    }

    /// <summary>The list as the launcher-data sync wants it.</summary>
    public IReadOnlyList<ClientManifest.Entry> ToClientEntries() =>
        Mods.Select(m => new ClientManifest.Entry(m.Id, m.Version, m.Source)).ToList();

    /// <summary>The shared load order, in the shape the sync expects.</summary>
    public LoadOrder.Result ToOrder() => new(Mods.Select(m => m.Id).ToList(), []);

    /// <summary>
    /// A profile another host can run: same mods, same roles, same order. Copies are resolved on this PC by id, so
    /// the exporter's folder layout does not have to be reproduced.
    /// </summary>
    public Profile ToProfile(string name) => new()
    {
        Name = name,
        Mods = Mods.Select(m => new ProfileMod
        {
            Id = m.Id,
            Role = m.Role,
            Enabled = true,
            ServerAuthoritative = m.ServerAuthoritative,
            LastVersion = m.Version,
        }).ToList(),
    };
}
