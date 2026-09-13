using System.Text.Json;
using System.Text.Json.Serialization;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Compat;

/// <summary>Curated verdict for one mod under Coop. Unknown = no record.</summary>
public enum CompatVerdict { Unknown, Works, NeedsRecipe, Broken }

public enum CompatSource { None, Bundled, Local }

/// <summary>
/// One curated entry: what is known about a mod on the Coop dedicated server and which launcher defaults to use for it.
/// Any field left null means "no opinion"; consumers fall back to their own defaults.
/// </summary>
public sealed class CompatRecord
{
    public string Id { get; set; } = "";
    public CompatVerdict Verdict { get; set; } = CompatVerdict.Unknown;
    /// <summary>Mod versions the verdict was checked against (manifest form, e.g. "v4.2.0.7").</summary>
    public List<string> TestedVersions { get; set; } = new();
    public string? TestedCoopVersion { get; set; }
    public ServerRole? DefaultRole { get; set; }
    public bool? ServerAuthoritative { get; set; }
    public List<string> ClientSideBehaviors { get; set; } = new();
    /// <summary>Submodule class types that stay loaded when the mod runs DependencyOnly (a headless settings core, for example).</summary>
    public List<string> KeepSubModules { get; set; } = new();
    /// <summary>Hints for the live Mod settings discovery: force these plain settings classes in (full type names or trailing-* globs).</summary>
    public List<string> SettingsTypes { get; set; } = new();
    /// <summary>Hints: never treat these classes as settings.</summary>
    public List<string> IgnoreSettingsTypes { get; set; } = new();
    /// <summary>Lines the mod's own config files must contain under Coop, applied to its folder before launch. See <see cref="EnsureLinesApplier"/>.</summary>
    public List<EnsureLine> EnsureLines { get; set; } = new();
    /// <summary>
    /// Mod setting values this mod needs under Coop: settingsId → propId → text (the live-settings wire form). Staged as
    /// host overrides at launch unless the profile already overrides that property; settings sync carries them to clients.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> DefaultSettings { get; set; } = new(StringComparer.Ordinal);
    public string? Notes { get; set; }
    public string? Url { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public bool IsVersionTested(string? version) => version is not null && TestedVersions.Any(v => SaveHeaderReader.VersionsEqual(v, version));

    public CompatRecord Clone() => new()
    {
        Id = Id, Verdict = Verdict, TestedVersions = TestedVersions.ToList(), TestedCoopVersion = TestedCoopVersion,
        DefaultRole = DefaultRole, ServerAuthoritative = ServerAuthoritative, ClientSideBehaviors = ClientSideBehaviors.ToList(),
        KeepSubModules = KeepSubModules.ToList(), SettingsTypes = SettingsTypes.ToList(), IgnoreSettingsTypes = IgnoreSettingsTypes.ToList(),
        EnsureLines = EnsureLines.Select(l => new EnsureLine { File = l.File, Section = l.Section, Value = l.Value }).ToList(),
        DefaultSettings = DefaultSettings.ToDictionary(o => o.Key, o => new Dictionary<string, string>(o.Value, StringComparer.Ordinal), StringComparer.Ordinal),
        Notes = Notes, Url = Url, UpdatedAt = UpdatedAt,
    };
}

/// <summary>What the Mods tab shows for one mod: the verdict, whether this exact version was tested, and where the record came from.</summary>
public sealed record CompatBadge(CompatVerdict Verdict, bool VersionUntested, CompatSource Source, CompatRecord? Record)
{
    public static readonly CompatBadge None = new(CompatVerdict.Unknown, false, CompatSource.None, null);
}

/// <summary>The on-disk shape shared by the bundled file, the local override and export files.</summary>
public sealed class CompatDbFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<CompatRecord> Records { get; set; } = new();
}

/// <summary>
/// Bundled compat-db.json (next to the exe) merged with the user's compat-db.local.json (whole record by id, local wins).
/// Replaces the hardcoded per-mod role and keep-submodule tables; those values remain as the fallback when no file ships.
/// </summary>
public sealed class CompatDb
{
    public const int CurrentSchemaVersion = 1;
    public const string BundledFileName = "compat-db.json";
    public const string LocalFileName = "compat-db.local.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, (CompatRecord Record, CompatSource Source)> _records = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Problems { get; }
    public IEnumerable<CompatRecord> Records => _records.Values.Select(v => v.Record);
    public IEnumerable<CompatRecord> LocalRecords => _records.Values.Where(v => v.Source == CompatSource.Local).Select(v => v.Record);
    public IEnumerable<CompatRecord> BundledRecords => _records.Values.Where(v => v.Source == CompatSource.Bundled).Select(v => v.Record);

    private CompatDb(IEnumerable<CompatRecord> bundled, IEnumerable<CompatRecord> local, List<string> problems)
    {
        foreach (var r in bundled) if (!string.IsNullOrWhiteSpace(r.Id)) _records[r.Id] = (r, CompatSource.Bundled);
        foreach (var r in local) if (!string.IsNullOrWhiteSpace(r.Id)) _records[r.Id] = (r, CompatSource.Local);
        Problems = problems;
    }

    // ---- locations ---------------------------------------------------------------------------------------------

    /// <summary>Where the release keeps data files, so the unzipped folder is not a wall of loose files.</summary>
    public const string DataFolder = "data";

    /// <summary>The bundled database: under data\ in a release, next to the exe in a dev build.</summary>
    public static string BundledPath => BundledPathIn(AppContext.BaseDirectory);

    /// <summary>The same lookup against a given folder, so the order of preference can be tested.</summary>
    public static string BundledPathIn(string baseDir)
    {
        var inData = Path.Combine(baseDir, DataFolder, BundledFileName);
        return File.Exists(inData) ? inData : Path.Combine(baseDir, BundledFileName);
    }
    public static string LocalPath => Path.Combine(ProfileStore.RootDir, LocalFileName);

    private static CompatDb? _current;
    /// <summary>The process-wide DB (bundled + local). Reload() after writing the local file.</summary>
    public static CompatDb Current => _current ??= Load(BundledPath, LocalPath);
    public static CompatDb Reload() => _current = Load(BundledPath, LocalPath);

    // ---- loading -----------------------------------------------------------------------------------------------

    public static CompatDb Load(string? bundledPath, string? localPath)
    {
        var problems = new List<string>();
        var bundled = ReadFile(bundledPath, problems);
        var local = ReadFile(localPath, problems);
        return new CompatDb(bundled, local, problems);
    }

    public static CompatDb Empty() => new(Array.Empty<CompatRecord>(), Array.Empty<CompatRecord>(), new List<string>());

    private static List<CompatRecord> ReadFile(string? path, List<string> problems)
    {
        if (path is null || !File.Exists(path)) return new List<CompatRecord>();
        try { return Parse(File.ReadAllText(path)).Records; }
        catch (Exception ex) { problems.Add($"{Path.GetFileName(path)}: {ex.Message}"); return new List<CompatRecord>(); }
    }

    public static CompatDbFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CompatDbFile>(json, Json) ?? throw new InvalidDataException("empty file");
        if (file.SchemaVersion > CurrentSchemaVersion) throw new InvalidDataException($"schema {file.SchemaVersion} is newer than this launcher understands ({CurrentSchemaVersion})");
        foreach (var r in file.Records) r.Id = r.Id?.Trim() ?? "";
        return file;
    }

    public static string Serialize(IEnumerable<CompatRecord> records) =>
        JsonSerializer.Serialize(new CompatDbFile { Records = records.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList() }, Json);

    /// <summary>Atomic write (tmp + move), same convention as ProfileStore.Save.</summary>
    public static void WriteFile(string path, IEnumerable<CompatRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(records));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- queries -----------------------------------------------------------------------------------------------

    public CompatRecord? Find(string id) => _records.TryGetValue(id, out var v) ? v.Record : null;
    public CompatSource SourceOf(string id) => _records.TryGetValue(id, out var v) ? v.Source : CompatSource.None;

    public CompatBadge For(string id, string? version)
    {
        if (!_records.TryGetValue(id, out var v)) return CompatBadge.None;
        var untested = v.Record.TestedVersions.Count > 0 && !v.Record.IsVersionTested(version);
        return new CompatBadge(v.Record.Verdict, untested, v.Source, v.Record);
    }

    /// <summary>Role for a mod new to a profile: the record's DefaultRole, else the pre-DB table, else Run.</summary>
    public ServerRole DefaultRoleFor(string id) =>
        Find(id)?.DefaultRole ?? (FallbackRoles.TryGetValue(id, out var r) ? r : ServerRole.Run);

    /// <summary>Union of every record's KeepSubModules, plus the pre-DB MCM entries when no record covers MCM.</summary>
    public IReadOnlyCollection<string> KeepForDependencyOnly()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Records) foreach (var k in r.KeepSubModules) set.Add(k);
        if (Find(McmId) is null) foreach (var k in FallbackKeepSubModules) set.Add(k);
        return set;
    }

    // ---- local override ----------------------------------------------------------------------------------------

    /// <summary>Writes (or replaces) one record in the local override file. Caller does Reload() afterwards.</summary>
    public static void SaveLocal(string localPath, CompatRecord record)
    {
        var existing = ReadFile(localPath, new List<string>());
        existing.RemoveAll(r => r.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        record.UpdatedAt ??= DateTime.UtcNow;
        existing.Add(record);
        WriteFile(localPath, existing);
    }

    public static void RemoveLocal(string localPath, string id)
    {
        var existing = ReadFile(localPath, new List<string>());
        if (existing.RemoveAll(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0) return;
        if (existing.Count == 0) { if (File.Exists(localPath)) File.Delete(localPath); return; }
        WriteFile(localPath, existing);
    }

    public sealed record ImportResult(IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> Kept);

    /// <summary>
    /// Merges an exported file into the local override by id. An incoming record wins when the local one is missing or
    /// has an older (or no) UpdatedAt; otherwise the local record is kept and reported so the user can decide.
    /// </summary>
    public static ImportResult ImportLocal(string localPath, string importJson)
    {
        var incoming = Parse(importJson).Records.Where(r => r.Id.Length > 0).ToList();
        var existing = ReadFile(localPath, new List<string>());
        var added = new List<string>(); var updated = new List<string>(); var kept = new List<string>();
        foreach (var inc in incoming)
        {
            var cur = existing.FirstOrDefault(r => r.Id.Equals(inc.Id, StringComparison.OrdinalIgnoreCase));
            if (cur is null) { existing.Add(inc); added.Add(inc.Id); continue; }
            var incAt = inc.UpdatedAt ?? DateTime.MinValue;
            var curAt = cur.UpdatedAt ?? DateTime.MinValue;
            if (incAt > curAt) { existing.Remove(cur); existing.Add(inc); updated.Add(inc.Id); }
            else kept.Add(inc.Id);
        }
        if (added.Count + updated.Count > 0) WriteFile(localPath, existing);
        return new ImportResult(added, updated, kept);
    }

    // ---- fallbacks (the tables that existed before the DB) -----------------------------------------------------

    private const string McmId = "Bannerlord.MBOptionScreen";

    public static readonly IReadOnlyDictionary<string, ServerRole> FallbackRoles = new Dictionary<string, ServerRole>(StringComparer.OrdinalIgnoreCase)
    {
        ["Bannerlord.Harmony"] = ServerRole.DependencyOnly,
        ["Bannerlord.ButterLib"] = ServerRole.DependencyOnly,
        ["Bannerlord.UIExtenderEx"] = ServerRole.DependencyOnly,
        [McmId] = ServerRole.DependencyOnly,
    };

    /// <summary>MCM's settings core runs headless; its UI submodules do not.</summary>
    public static readonly string[] FallbackKeepSubModules = ["MCM.MCMSubModule", "MCM.Internal.MCMImplementationSubModule"];
}
