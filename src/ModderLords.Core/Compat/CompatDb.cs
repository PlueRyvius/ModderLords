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
    /// <summary>
    /// The mod patches Coop and must load AFTER it on the player's machine, even though its manifest never says so.
    /// CoopMarriage's Harmony patches target Coop's own types: load it first and TargetMethod() returns null,
    /// PatchAll throws out of OnSubModuleLoad and Bannerlord dies at startup with 0xE0434352.
    ///
    /// The dedicated server pins Coop after the community block regardless, so this only ever moves a client order.
    /// </summary>
    public bool? ClientLoadsAfterCoop { get; set; }
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
        ClientLoadsAfterCoop = ClientLoadsAfterCoop,
        EnsureLines = EnsureLines.Select(l => new EnsureLine { File = l.File, Section = l.Section, Value = l.Value }).ToList(),
        DefaultSettings = DefaultSettings.ToDictionary(o => o.Key, o => new Dictionary<string, string>(o.Value, StringComparer.Ordinal), StringComparer.Ordinal),
        Notes = Notes, Url = Url, UpdatedAt = UpdatedAt,
    };
}

/// <summary>
/// What the Mods tab shows for one mod: the verdict, whether this exact version was tested, and where the record came from.
/// <paramref name="Record"/> is the effective record. <paramref name="OverBundled"/> is true when that is the user's local
/// record laid over a bundled one (<see cref="CompatDb.Merge"/>); the source is still Local, because the user has a say in it.
/// </summary>
public sealed record CompatBadge(CompatVerdict Verdict, bool VersionUntested, CompatSource Source, CompatRecord? Record, bool OverBundled = false)
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
/// Bundled compat-db.json (next to the exe) merged with the user's compat-db.local.json: by id, and field by field where
/// both files have the mod, so a local record only covers what it actually says (see <see cref="Merge"/>).
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

    /// <summary>The effective record per id: what every launch-time consumer and the badge read.</summary>
    private readonly Dictionary<string, (CompatRecord Record, CompatSource Source)> _records = new(StringComparer.OrdinalIgnoreCase);
    // The two files as read, kept apart from the effective records: Export and the Record dialog must work from what the
    // user actually stored, or a merged record would be written back and pin every bundled value it happened to contain.
    private readonly Dictionary<string, CompatRecord> _bundled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompatRecord> _local = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Problems { get; }
    /// <summary>Effective records: bundled, local, or a local one laid over the bundled one.</summary>
    public IEnumerable<CompatRecord> Records => _records.Values.Select(v => v.Record);
    /// <summary>The user's records exactly as stored in the local file, never merged. This is what Export shares.</summary>
    public IEnumerable<CompatRecord> LocalRecords => _local.Values;
    /// <summary>Records the user has said nothing about, so the bundled one is shown as it ships.</summary>
    public IEnumerable<CompatRecord> BundledRecords => _records.Values.Where(v => v.Source == CompatSource.Bundled).Select(v => v.Record);

    private CompatDb(IEnumerable<CompatRecord> bundled, IEnumerable<CompatRecord> local, List<string> problems)
    {
        foreach (var r in bundled)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) continue;
            _bundled[r.Id] = r;
            _records[r.Id] = (r, CompatSource.Bundled);
        }
        foreach (var r in local)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) continue;
            _local[r.Id] = r;
            _records[r.Id] = (_bundled.TryGetValue(r.Id, out var b) ? Merge(b, r) : r, CompatSource.Local);
        }
        Problems = problems;
    }

    // ---- merging -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The effective record for a mod both files know: the bundled record with the local record's <em>set</em> fields
    /// laid over it. Taking the local record whole (the earlier rule) meant that recording anything for a mod froze every
    /// other field at whatever the bundled record said that day, so later bundled fixes never reached that user.
    ///
    /// "Set" means non-null, a non-empty list or dictionary, non-blank text, or a verdict other than Unknown. Per field:
    /// <list type="bullet">
    /// <item><c>Id</c>: the bundled spelling; ids match case-insensitively and the bundled one is the curated form.</item>
    /// <item><c>Verdict</c>, <c>TestedVersions</c>, <c>TestedCoopVersion</c>, <c>Notes</c>: one block, the test report. When
    /// the local record has a verdict all four come from it, empty or not: they say what <em>this user</em> tested, and
    /// the bundled versions or notes under someone else's verdict would claim a test nobody ran. Without a local verdict
    /// the bundled verdict stands and each of the other three falls back on its own, like any other field.</item>
    /// <item><c>UpdatedAt</c>: local when set, else bundled. SaveLocal always stamps it, so it dates the user's part.</item>
    /// <item><c>DefaultRole</c>, <c>ServerAuthoritative</c>, <c>ClientLoadsAfterCoop</c>, <c>Url</c>: local when set, else bundled.</item>
    /// <item><c>ClientSideBehaviors</c>, <c>KeepSubModules</c>, <c>SettingsTypes</c>, <c>IgnoreSettingsTypes</c>,
    /// <c>EnsureLines</c>: the local list when it has entries, else the bundled list. Replaced, not unioned: a user who
    /// states a list must be able to leave a bundled entry out of it.</item>
    /// <item><c>DefaultSettings</c>: merged per settingsId and per property; a local value wins, bundled values for other
    /// properties stay. Here the key being present is what counts as set, so a local value may be the empty string.</item>
    /// </list>
    ///
    /// The price: a local record cannot blank a bundled value any more. An empty local list, null, blank notes or an
    /// Unknown verdict all read as "no opinion" and the bundled value shows through; to switch something off the local
    /// record has to say something else (a different role, a list with other entries, a setting's other value).
    ///
    /// Every public property of <see cref="CompatRecord"/> must be handled here and in <see cref="LocalPart"/>;
    /// CompatDbTests walks the properties by reflection and fails for one that is not.
    /// </summary>
    public static CompatRecord Merge(CompatRecord bundled, CompatRecord local)
    {
        var m = bundled.Clone();
        var l = local.Clone(); // so the merged record shares no list with the stored local one

        if (l.Verdict != CompatVerdict.Unknown)
        {
            m.Verdict = l.Verdict;
            m.TestedVersions = l.TestedVersions;
            m.TestedCoopVersion = l.TestedCoopVersion;
            m.Notes = l.Notes;
        }
        else
        {
            if (l.TestedVersions.Count > 0) m.TestedVersions = l.TestedVersions;
            m.TestedCoopVersion = l.TestedCoopVersion ?? m.TestedCoopVersion;
            if (!string.IsNullOrWhiteSpace(l.Notes)) m.Notes = l.Notes;
        }
        m.UpdatedAt = l.UpdatedAt ?? m.UpdatedAt;

        m.DefaultRole = l.DefaultRole ?? m.DefaultRole;
        m.ServerAuthoritative = l.ServerAuthoritative ?? m.ServerAuthoritative;
        m.ClientLoadsAfterCoop = l.ClientLoadsAfterCoop ?? m.ClientLoadsAfterCoop;
        if (!string.IsNullOrWhiteSpace(l.Url)) m.Url = l.Url;

        if (l.ClientSideBehaviors.Count > 0) m.ClientSideBehaviors = l.ClientSideBehaviors;
        if (l.KeepSubModules.Count > 0) m.KeepSubModules = l.KeepSubModules;
        if (l.SettingsTypes.Count > 0) m.SettingsTypes = l.SettingsTypes;
        if (l.IgnoreSettingsTypes.Count > 0) m.IgnoreSettingsTypes = l.IgnoreSettingsTypes;
        if (l.EnsureLines.Count > 0) m.EnsureLines = l.EnsureLines;

        foreach (var (settingsId, props) in l.DefaultSettings)
        {
            if (!m.DefaultSettings.TryGetValue(settingsId, out var target))
                m.DefaultSettings[settingsId] = target = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (prop, value) in props) target[prop] = value;
        }
        return m;
    }

    /// <summary>
    /// The inverse of <see cref="Merge"/>, for the Record dialog: given the effective record as the user left it, the
    /// local record to store so that merging it over <paramref name="bundled"/> gives that record back. Anything still
    /// equal to the bundled value is left unset, which is the whole point: the dialog shows the effective record, and
    /// saving that as it stands would copy every bundled value into the local file and freeze it there.
    ///
    /// Null when nothing differs, so the caller keeps no local record at all. With no bundled record the edited record
    /// is the local record. A field the user blanked comes out unset and the bundled value returns (see Merge).
    /// </summary>
    public static CompatRecord? LocalPart(CompatRecord edited, CompatRecord? bundled)
    {
        if (bundled is null) return edited;
        var e = edited.Clone();
        var part = new CompatRecord { Id = e.Id };

        var sameVersions = e.TestedVersions.SequenceEqual(bundled.TestedVersions, StringComparer.Ordinal);
        var sameNotes = Text(e.Notes) == Text(bundled.Notes);
        if (e.Verdict != CompatVerdict.Unknown)
        {
            // The report travels as a block (Merge), so one changed part stores all four.
            if (e.Verdict != bundled.Verdict || !sameVersions || e.TestedCoopVersion != bundled.TestedCoopVersion || !sameNotes)
            {
                part.Verdict = e.Verdict;
                part.TestedVersions = e.TestedVersions;
                part.TestedCoopVersion = e.TestedCoopVersion;
                part.Notes = e.Notes;
            }
        }
        else
        {
            if (!sameVersions) part.TestedVersions = e.TestedVersions;
            if (e.TestedCoopVersion != bundled.TestedCoopVersion) part.TestedCoopVersion = e.TestedCoopVersion;
            if (!sameNotes) part.Notes = e.Notes;
        }

        if (e.DefaultRole != bundled.DefaultRole) part.DefaultRole = e.DefaultRole;
        if (e.ServerAuthoritative != bundled.ServerAuthoritative) part.ServerAuthoritative = e.ServerAuthoritative;
        if (e.ClientLoadsAfterCoop != bundled.ClientLoadsAfterCoop) part.ClientLoadsAfterCoop = e.ClientLoadsAfterCoop;
        if (Text(e.Url) != Text(bundled.Url)) part.Url = e.Url;

        if (!e.ClientSideBehaviors.SequenceEqual(bundled.ClientSideBehaviors, StringComparer.Ordinal)) part.ClientSideBehaviors = e.ClientSideBehaviors;
        if (!e.KeepSubModules.SequenceEqual(bundled.KeepSubModules, StringComparer.Ordinal)) part.KeepSubModules = e.KeepSubModules;
        if (!e.SettingsTypes.SequenceEqual(bundled.SettingsTypes, StringComparer.Ordinal)) part.SettingsTypes = e.SettingsTypes;
        if (!e.IgnoreSettingsTypes.SequenceEqual(bundled.IgnoreSettingsTypes, StringComparer.Ordinal)) part.IgnoreSettingsTypes = e.IgnoreSettingsTypes;
        if (!e.EnsureLines.Select(LineKey).SequenceEqual(bundled.EnsureLines.Select(LineKey))) part.EnsureLines = e.EnsureLines;

        foreach (var (settingsId, props) in e.DefaultSettings)
        {
            bundled.DefaultSettings.TryGetValue(settingsId, out var known);
            var changed = props.Where(p => known is null || !known.TryGetValue(p.Key, out var v) || v != p.Value)
                               .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (changed.Count > 0) part.DefaultSettings[settingsId] = changed;
        }

        // Compared as text against a record with nothing in it, so a field added later cannot be forgotten here.
        if (Serialize([part]) == Serialize([new CompatRecord { Id = part.Id }])) return null;
        part.UpdatedAt = e.UpdatedAt;
        return part;
    }

    private static string Text(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    private static (string, string?, string) LineKey(EnsureLine l) => (l.File, l.Section, l.Value);

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

    /// <summary>The effective record: what the launcher acts on.</summary>
    public CompatRecord? Find(string id) => _records.TryGetValue(id, out var v) ? v.Record : null;
    /// <summary>The user's own record as stored, without the bundled values that show through it.</summary>
    public CompatRecord? FindLocal(string id) => _local.GetValueOrDefault(id);
    /// <summary>The bundled record as shipped, whether or not a local record is laid over it.</summary>
    public CompatRecord? FindBundled(string id) => _bundled.GetValueOrDefault(id);
    /// <summary>Local whenever the user has a record for the mod, merged over a bundled one or not.</summary>
    public CompatSource SourceOf(string id) => _records.TryGetValue(id, out var v) ? v.Source : CompatSource.None;

    public CompatBadge For(string id, string? version)
    {
        if (!_records.TryGetValue(id, out var v)) return CompatBadge.None;
        var untested = v.Record.TestedVersions.Count > 0 && !v.Record.IsVersionTested(version);
        return new CompatBadge(v.Record.Verdict, untested, v.Source, v.Record, v.Source == CompatSource.Local && _bundled.ContainsKey(id));
    }

    /// <summary>Role for a mod new to a profile: the record's DefaultRole, else the pre-DB table, else Run.</summary>
    public ServerRole DefaultRoleFor(string id) =>
        Find(id)?.DefaultRole ?? (FallbackRoles.TryGetValue(id, out var r) ? r : ServerRole.Run);

    /// <summary>
    /// Mods curated as having to load after Coop on a player's machine. Manifests do not carry this: CoopMarriage
    /// and CoopModPatch declare nothing about Coop at all, so the sorter cannot derive it and only a curated fact
    /// keeps them behind it. Fed to <c>LoadOrder.Compute</c> as <c>knownToFollowCoop</c>.
    /// </summary>
    public IReadOnlyCollection<string> ClientFollowsCoop() =>
        Records.Where(r => r.ClientLoadsAfterCoop == true).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Union of every record's KeepSubModules, plus the pre-DB MCM entries when no record covers MCM.</summary>
    public IReadOnlyCollection<string> KeepForDependencyOnly()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Records) foreach (var k in r.KeepSubModules) set.Add(k);
        if (Find(McmId) is null) foreach (var k in FallbackKeepSubModules) set.Add(k);
        return set;
    }

    // ---- local override ----------------------------------------------------------------------------------------

    /// <summary>
    /// Writes (or replaces) one record in the local override file. Caller does Reload() afterwards.
    /// Pass the user's own part (<see cref="LocalPart"/>), never an effective record from <see cref="Find"/>.
    /// </summary>
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
