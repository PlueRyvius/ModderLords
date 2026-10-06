using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Compat;

/// <summary>
/// The gate a compatibility record passes through before it may enter the bundled compat-db.json: the "Compat record"
/// issue form on GitHub, the workflow that turns a submission into a pull request, and the CI check on the committed
/// file all run this code (through tools/CompatRecordIntake).
///
/// It lives in Core, next to <see cref="CompatDb"/>, so the gate and the launcher read one schema. It is deliberately
/// stricter than <see cref="CompatDb.Parse"/>: the launcher must keep loading a file written by a newer build, so it
/// ignores what it does not know; a submission is somebody else's text headed for every host's machine, so here an
/// unknown property, a misspelt enum value or a path that climbs out of the mod's folder is an error, not a shrug.
///
/// Everything is pure text in, text out. See docs/COMPAT-RECORDS.md.
/// </summary>
public static partial class CompatIntake
{
    // ---- the issue form (.github/ISSUE_TEMPLATE/compat-record.yml) ---------------------------------------------

    // GitHub renders a submitted form as "### <label>" headings, not ids, so these labels are part of the contract
    // with the template. CompatIntakeTests reads the template and fails when the two drift apart.
    public const string ModLabel = "Module id";
    public const string ModVersionLabel = "Mod version";
    public const string CoopVersionLabel = "Coop version";
    public const string RecordLabel = "Compatibility record";
    public const string NotesLabel = "Notes";

    public static readonly IReadOnlyList<string> FormLabels = [ModLabel, ModVersionLabel, CoopVersionLabel, RecordLabel, NotesLabel];

    /// <summary>What GitHub writes for an optional field that was left empty.</summary>
    private const string NoResponse = "_No response_";

    // ---- limits ------------------------------------------------------------------------------------------------

    /// <summary>The whole submitted record, as UTF-8. The largest hand-written record today is under 4 KB.</summary>
    public const int MaxRecordBytes = 32 * 1024;
    public const int MaxIdLength = 64;
    public const int MaxVersionLength = 40;
    public const int MaxTypeNameLength = 200;
    public const int MaxListItems = 50;
    public const int MaxTestedVersions = 20;
    public const int MaxNotesLength = 8000;
    public const int MaxUrlLength = 300;
    public const int MaxEnsureLines = 10;
    public const int MaxEnsureFileLength = 100;
    public const int MaxEnsureSectionLength = 100;
    public const int MaxEnsureValueLength = 300;
    public const int MaxSettingsGroups = 20;
    public const int MaxSettingsPerGroup = 100;
    public const int MaxSettingKeyLength = 200;
    public const int MaxSettingValueLength = 500;

    /// <summary>
    /// A module id as it may appear in a branch-free but still public place: the commit message and the pull request
    /// title. Letters, digits, dot, underscore and hyphen only, so nothing in it can be markdown, a mention or a shell
    /// metacharacter.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    /// <summary>The form's free-text version fields, when they are shown back in the pull request.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,39}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]+$")]
    private static partial Regex PathSegmentPattern();

    /// <summary>
    /// File types <see cref="EnsureLinesApplier"/> may be pointed at: line-based text configuration. Appending a line
    /// to an XML or JSON file breaks it, and appending one to a DLL breaks the mod for every host at once.
    /// </summary>
    public static readonly IReadOnlyList<string> EnsureFileExtensions = [".txt", ".ini", ".cfg", ".conf", ".toml", ".properties"];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Every property a record may carry. A test holds this against <see cref="CompatRecord"/>'s own properties.</summary>
    public static readonly IReadOnlyList<string> RecordProperties =
    [
        nameof(CompatRecord.Id), nameof(CompatRecord.Verdict), nameof(CompatRecord.TestedVersions), nameof(CompatRecord.TestedCoopVersion),
        nameof(CompatRecord.DefaultRole), nameof(CompatRecord.ServerAuthoritative), nameof(CompatRecord.ClientSideBehaviors),
        nameof(CompatRecord.KeepSubModules), nameof(CompatRecord.SettingsTypes), nameof(CompatRecord.IgnoreSettingsTypes),
        nameof(CompatRecord.ClientLoadsAfterCoop), nameof(CompatRecord.ServerExcludedFolders), nameof(CompatRecord.ClientExcludedFolders),
        nameof(CompatRecord.EnsureLines),
        nameof(CompatRecord.DefaultSettings),
        nameof(CompatRecord.Notes), nameof(CompatRecord.Url), nameof(CompatRecord.UpdatedAt),
    ];

    private static readonly JsonDocumentOptions StrictJson = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    // ---- reading the issue body --------------------------------------------------------------------------------

    /// <summary>The form's fields as submitted. Null: the field was left empty or its heading is missing.</summary>
    public sealed record IssueForm(string? Mod, string? ModVersion, string? CoopVersion, string? Record, string? Notes);

    /// <summary>
    /// Splits a rendered issue-form body into its fields. Only the form's own headings count, and only outside a code
    /// fence, so a "### Module id" line pasted into the record or the notes cannot stand in for the real field. A
    /// heading that appears twice is reported: the body was edited by hand into something ambiguous.
    /// </summary>
    public static IssueForm ParseIssueBody(string? body, List<string> problems)
    {
        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        StringBuilder? current = null;
        var inFence = false;
        foreach (var line in (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                // An opening fence may carry an info string ("```json"); a closing fence is bare.
                if (!inFence) inFence = true;
                else if (trimmed.Trim('`').Length == 0) inFence = false;
            }
            else if (!inFence && line.StartsWith("### ", StringComparison.Ordinal) && FormLabels.Contains(line[4..].Trim()))
            {
                var label = line[4..].Trim();
                if (sections.ContainsKey(label)) problems.Add($"The form section \"{label}\" appears more than once. Please submit the form again without editing its headings.");
                sections[label] = current = new StringBuilder();
                continue;
            }
            current?.Append(line).Append('\n');
        }

        string? Field(string label)
        {
            if (!sections.TryGetValue(label, out var sb)) return null;
            var value = sb.ToString().Trim();
            return value.Length == 0 || value == NoResponse ? null : value;
        }

        return new IssueForm(Field(ModLabel), Field(ModVersionLabel), Field(CoopVersionLabel), StripFence(Field(RecordLabel)), Field(NotesLabel));
    }

    /// <summary>Removes the ```json fence GitHub wraps a <c>render: json</c> textarea in. A body without one is taken as it is.</summary>
    private static string? StripFence(string? value)
    {
        if (value is null || !value.StartsWith("```", StringComparison.Ordinal)) return value;
        var firstBreak = value.IndexOf('\n');
        if (firstBreak < 0) return null;
        var inner = value[(firstBreak + 1)..];
        var close = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (close >= 0 && inner[(close + 3)..].Trim().Length == 0) inner = inner[..close];
        inner = inner.Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>A form version field that is safe to show back, or null when it is empty or holds anything unexpected.</summary>
    public static string? SafeVersion(string? value) => value is not null && VersionPattern().IsMatch(value.Trim()) ? value.Trim() : null;

    // ---- validating one submitted record -----------------------------------------------------------------------

    /// <summary>A submission's outcome: the record when <see cref="Problems"/> is empty, otherwise why it was refused.</summary>
    public sealed record Submission(CompatRecord? Record, IReadOnlyList<string> Problems)
    {
        public bool IsValid => Record is not null && Problems.Count == 0;
    }

    /// <summary>Reads the issue body and validates the record in it against the form's own module id field.</summary>
    public static Submission ValidateIssue(string? issueBody)
    {
        var problems = new List<string>();
        var form = ParseIssueBody(issueBody, problems);
        if (form.Mod is null) problems.Add($"The \"{ModLabel}\" field is empty or missing.");
        if (form.Record is null) problems.Add($"The \"{RecordLabel}\" field is empty or missing. It should hold the JSON that ModderLords exported for the mod.");
        if (problems.Count > 0) return new Submission(null, problems);
        return ValidateSubmission(form.Record!, form.Mod);
    }

    /// <summary>
    /// Validates one submitted record: a single record object, or the export-file shape with exactly one record.
    /// <paramref name="expectedId"/> is the form's module id field; the record must be for that mod.
    /// </summary>
    public static Submission ValidateSubmission(string json, string? expectedId)
    {
        var problems = new List<string>();
        if (Encoding.UTF8.GetByteCount(json) > MaxRecordBytes)
            return new Submission(null, [$"The record is larger than {MaxRecordBytes / 1024} KB. A record is a few lines about one mod; please shorten the notes."]);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, StrictJson); }
        catch (JsonException ex) { return new Submission(null, [$"The record is not valid JSON: {Quote(ex.Message, 200)}"]); }

        using (doc)
        {
            var element = SingleRecord(doc.RootElement, problems);
            if (element is null) return new Submission(null, problems);

            // Reading a string with a broken \u escape throws rather than returning text.
            try { CheckRecord(element.Value, problems); }
            catch (InvalidOperationException) { problems.Add("The record contains text that is not valid Unicode (a broken \\u escape)."); }
            if (problems.Count > 0) return new Submission(null, problems);

            CompatRecord record;
            try { record = CompatDb.Parse("{\"SchemaVersion\":1,\"Records\":[" + element.Value.GetRawText() + "]}").Records.Single(); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                return new Submission(null, [$"ModderLords could not read the record: {Quote(ex.Message, 200)}"]);
            }

            if (expectedId is not null && !string.Equals(record.Id, expectedId.Trim(), StringComparison.Ordinal))
                problems.Add($"The record is for {Quote(record.Id)}, but the \"{ModLabel}\" field says {Quote(expectedId.Trim())}. They must be the same mod, spelt the same way.");
            return problems.Count > 0 ? new Submission(null, problems) : new Submission(record, problems);
        }
    }

    /// <summary>The record element out of either accepted shape, or null (with the reason added) when there is not exactly one.</summary>
    private static JsonElement? SingleRecord(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("The record must be a JSON object: one record, or an export file holding exactly one record.");
            return null;
        }
        var isFile = root.EnumerateObject().Any(p => p.Name is nameof(CompatDbFile.Records) or nameof(CompatDbFile.SchemaVersion));
        if (!isFile) return root;

        JsonElement? records = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name)) { problems.Add($"The export file has the property {Quote(p.Name)} more than once."); continue; }
            switch (p.Name)
            {
                case nameof(CompatDbFile.SchemaVersion):
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var schema) || schema < 1 || schema > CompatDb.CurrentSchemaVersion)
                        problems.Add($"SchemaVersion must be {CompatDb.CurrentSchemaVersion}.");
                    break;
                case nameof(CompatDbFile.Records):
                    records = p.Value;
                    break;
                default:
                    problems.Add($"The export file has an unknown property {Quote(p.Name)}. Only SchemaVersion and Records are allowed.");
                    break;
            }
        }
        if (records is not { ValueKind: JsonValueKind.Array })
        {
            problems.Add("The export file has no Records list.");
            return null;
        }
        var count = records.Value.GetArrayLength();
        if (count != 1)
        {
            problems.Add($"The export file holds {count} records. Submit exactly one record per issue.");
            return null;
        }
        return problems.Count > 0 ? null : records.Value[0];
    }

    // ---- the per-field rules -----------------------------------------------------------------------------------

    /// <summary>Checks one record object against every rule. Adds one plain sentence per problem.</summary>
    private static void CheckRecord(JsonElement record, List<string> problems)
    {
        if (record.ValueKind != JsonValueKind.Object) { problems.Add("A record must be a JSON object."); return; }
        if (Encoding.UTF8.GetByteCount(record.GetRawText()) > MaxRecordBytes) { problems.Add($"The record is larger than {MaxRecordBytes / 1024} KB."); return; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in record.EnumerateObject())
        {
            if (!seen.Add(p.Name)) { problems.Add($"The property {Quote(p.Name)} appears more than once."); continue; }
            var v = p.Value;
            switch (p.Name)
            {
                case nameof(CompatRecord.Id):
                    if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) problems.Add("Id is empty. It must be the mod's module id.");
                    else if (!IdPattern().IsMatch(v.GetString()!)) problems.Add($"Id {Quote(v.GetString()!)} is not a module id this form accepts: letters, digits, dot, underscore and hyphen, at most {MaxIdLength} characters, no spaces.");
                    break;
                case nameof(CompatRecord.Verdict):
                    CheckEnum<CompatVerdict>(p.Name, v, nullable: false, problems);
                    break;
                case nameof(CompatRecord.DefaultRole):
                    CheckEnum<ServerRole>(p.Name, v, nullable: true, problems);
                    break;
                case nameof(CompatRecord.TestedVersions):
                    CheckStringList(p.Name, v, MaxTestedVersions, MaxVersionLength, problems);
                    break;
                case nameof(CompatRecord.TestedCoopVersion):
                    CheckString(p.Name, v, MaxVersionLength, nullable: true, problems);
                    break;
                case nameof(CompatRecord.ServerAuthoritative):
                case nameof(CompatRecord.ClientLoadsAfterCoop):
                    if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) problems.Add($"{p.Name} must be true, false or null.");
                    break;
                case nameof(CompatRecord.ClientSideBehaviors):
                case nameof(CompatRecord.KeepSubModules):
                case nameof(CompatRecord.SettingsTypes):
                case nameof(CompatRecord.IgnoreSettingsTypes):
                    CheckStringList(p.Name, v, MaxListItems, MaxTypeNameLength, problems);
                    break;
                case nameof(CompatRecord.ServerExcludedFolders):
                case nameof(CompatRecord.ClientExcludedFolders):
                    CheckStringList(p.Name, v, MaxListItems, MaxTypeNameLength, problems);
                    // The launcher ignores a name it cannot honour and says so in the launch log; a curated record
                    // should not ship one for every host to be warned about.
                    if (v.ValueKind == JsonValueKind.Array)
                        foreach (var item in v.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                                && Overlay.ServerFolderExclusions.Problem(item.GetString()) is { } why)
                                problems.Add($"{p.Name} entry {Quote(item.GetString()!)} cannot be left out: {why}.");
                    break;
                case nameof(CompatRecord.EnsureLines):
                    CheckEnsureLines(v, problems);
                    break;
                case nameof(CompatRecord.DefaultSettings):
                    CheckDefaultSettings(v, problems);
                    break;
                case nameof(CompatRecord.Notes):
                    CheckString(p.Name, v, MaxNotesLength, nullable: true, problems);
                    break;
                case nameof(CompatRecord.Url):
                    if (CheckString(p.Name, v, MaxUrlLength, nullable: true, problems) && v.ValueKind == JsonValueKind.String) CheckUrl(v.GetString()!, problems);
                    break;
                case nameof(CompatRecord.UpdatedAt):
                    if (v.ValueKind != JsonValueKind.Null && (v.ValueKind != JsonValueKind.String || !v.TryGetDateTime(out _)))
                        problems.Add("UpdatedAt must be a date and time such as 2026-09-02T00:00:00Z.");
                    break;
                default:
                    problems.Add($"Unknown property {Quote(p.Name)}. Property names are case-sensitive; the known ones are {string.Join(", ", RecordProperties)}.");
                    break;
            }
        }
        if (!seen.Contains(nameof(CompatRecord.Id))) problems.Add("The record has no Id. It must be the mod's module id.");
    }

    private static void CheckEnum<T>(string name, JsonElement v, bool nullable, List<string> problems) where T : struct, Enum
    {
        if (nullable && v.ValueKind == JsonValueKind.Null) return;
        // Names only, spelt exactly. The launcher's converter would also take a number, including one no value has.
        if (v.ValueKind == JsonValueKind.String && Enum.GetNames<T>().Contains(v.GetString(), StringComparer.Ordinal)) return;
        var shown = v.ValueKind == JsonValueKind.String ? Quote(v.GetString()!) : Quote(v.GetRawText());
        problems.Add($"{name} is {shown}; it must be one of {string.Join(", ", Enum.GetNames<T>())}.");
    }

    /// <summary>True when the value is a usable string (or an allowed null); otherwise the reason has been added.</summary>
    private static bool CheckString(string name, JsonElement v, int maxLength, bool nullable, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!nullable) problems.Add($"{name} must be text, not null.");
            return nullable;
        }
        if (v.ValueKind != JsonValueKind.String) { problems.Add($"{name} must be text in double quotes."); return false; }
        return CheckText(name, v.GetString()!, maxLength, problems);
    }

    private static bool CheckText(string name, string s, int maxLength, List<string> problems)
    {
        if (s.Length > maxLength) { problems.Add($"{name} is {s.Length} characters long; the limit is {maxLength}."); return false; }
        if (FindForbiddenChar(s) is { } bad) { problems.Add($"{name} contains a control or invisible formatting character (U+{bad:X4}). Only ordinary visible text is accepted."); return false; }
        return true;
    }

    private static void CheckStringList(string name, JsonElement v, int maxItems, int maxLength, List<string> problems)
    {
        if (v.ValueKind != JsonValueKind.Array) { problems.Add($"{name} must be a list in square brackets (an empty list is [])."); return; }
        if (v.GetArrayLength() > maxItems) { problems.Add($"{name} has {v.GetArrayLength()} entries; the limit is {maxItems}."); return; }
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            i++;
            if (item.ValueKind != JsonValueKind.String) { problems.Add($"{name} entry {i} must be text in double quotes."); continue; }
            if (string.IsNullOrWhiteSpace(item.GetString())) { problems.Add($"{name} entry {i} is empty."); continue; }
            CheckText($"{name} entry {i}", item.GetString()!, maxLength, problems);
        }
    }

    private static void CheckUrl(string url, List<string> problems)
    {
        var ok = (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal))
                 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                 && uri.Host.Length > 0 && uri.UserInfo.Length == 0
                 && !url.Any(char.IsWhiteSpace);
        if (!ok) problems.Add("Url must be a plain http:// or https:// web address (no other scheme, no spaces, no user name in front of the host).");
    }

    /// <summary>
    /// EnsureLines makes the launcher write into a file on every host that uses the record, so the file must be a
    /// line-based config file strictly inside the mod's own folder. <see cref="EnsureLinesApplier"/> refuses an escaping
    /// path as well; this keeps such a record from ever being merged.
    /// </summary>
    private static void CheckEnsureLines(JsonElement v, List<string> problems)
    {
        if (v.ValueKind != JsonValueKind.Array) { problems.Add("EnsureLines must be a list in square brackets (an empty list is [])."); return; }
        if (v.GetArrayLength() > MaxEnsureLines) { problems.Add($"EnsureLines has {v.GetArrayLength()} entries; the limit is {MaxEnsureLines}."); return; }
        var i = 0;
        foreach (var line in v.EnumerateArray())
        {
            i++;
            var where = $"EnsureLines entry {i}";
            if (line.ValueKind != JsonValueKind.Object) { problems.Add($"{where} must be an object with File, Section and Value."); continue; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in line.EnumerateObject())
            {
                if (!seen.Add(p.Name)) { problems.Add($"{where} has the property {Quote(p.Name)} more than once."); continue; }
                switch (p.Name)
                {
                    case nameof(EnsureLine.File):
                        if (CheckString($"{where} File", p.Value, MaxEnsureFileLength, nullable: false, problems)) CheckEnsureFile(where, p.Value.GetString()!, problems);
                        break;
                    case nameof(EnsureLine.Section):
                        if (CheckString($"{where} Section", p.Value, MaxEnsureSectionLength, nullable: true, problems) && p.Value.ValueKind == JsonValueKind.String)
                        {
                            var section = p.Value.GetString()!;
                            if (section.Trim().Length == 0 || section.Contains('[') || section.Contains(']'))
                                problems.Add($"{where} Section must be a section name without brackets, or null.");
                        }
                        break;
                    case nameof(EnsureLine.Value):
                        if (CheckString($"{where} Value", p.Value, MaxEnsureValueLength, nullable: false, problems))
                        {
                            var value = p.Value.GetString()!;
                            if (value.Trim().Length == 0) problems.Add($"{where} Value is empty.");
                            else if (value.Replace(EnsureLinesApplier.CoopModuleIdToken, "", StringComparison.OrdinalIgnoreCase).Contains('{'))
                                problems.Add($"{where} Value contains a '{{' that is not the {EnsureLinesApplier.CoopModuleIdToken} token; the launcher would refuse to apply it.");
                        }
                        break;
                    default:
                        problems.Add($"{where} has an unknown property {Quote(p.Name)}. Only File, Section and Value are allowed.");
                        break;
                }
            }
            if (!seen.Contains(nameof(EnsureLine.File))) problems.Add($"{where} has no File.");
            if (!seen.Contains(nameof(EnsureLine.Value))) problems.Add($"{where} has no Value.");
        }
    }

    private static void CheckEnsureFile(string where, string file, List<string> problems)
    {
        void Refuse(string why) => problems.Add($"{where} File {Quote(file)} {why}");

        if (file.Length == 0 || file.Trim().Length == 0) { Refuse("is empty."); return; }
        if (file[0] is '/' or '\\' || file.Contains(':')) { Refuse("must be a path relative to the mod's folder, not an absolute path or a drive."); return; }
        foreach (var segment in file.Split('/', '\\'))
        {
            if (segment.Length == 0) { Refuse("has an empty path segment (a doubled or trailing separator)."); return; }
            if (segment is "." or "..") { Refuse("must stay inside the mod's folder; \".\" and \"..\" are not allowed."); return; }
            if (!PathSegmentPattern().IsMatch(segment)) { Refuse("may only use letters, digits, space, dot, underscore and hyphen in each folder and file name."); return; }
            if (segment[0] == ' ' || segment[^1] is ' ' or '.') { Refuse("has a folder or file name that starts with a space or ends with a space or dot."); return; }
            if (ReservedDeviceNames.Contains(segment.Split('.')[0])) { Refuse("uses a reserved Windows device name."); return; }
        }
        if (!EnsureFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            Refuse($"is not a line-based config file. Allowed file types: {string.Join(" ", EnsureFileExtensions)}.");
    }

    /// <summary>DefaultSettings: settings id, then property id, then the value as text. Every level is bounded and text-only.</summary>
    private static void CheckDefaultSettings(JsonElement v, List<string> problems)
    {
        if (v.ValueKind != JsonValueKind.Object) { problems.Add("DefaultSettings must be an object in curly braces (empty is {})."); return; }
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in v.EnumerateObject())
        {
            if (groups.Count >= MaxSettingsGroups) { problems.Add($"DefaultSettings covers more than {MaxSettingsGroups} settings ids."); return; }
            if (!groups.Add(group.Name)) { problems.Add($"DefaultSettings lists the settings id {Quote(group.Name)} more than once."); continue; }
            if (string.IsNullOrWhiteSpace(group.Name)) { problems.Add("DefaultSettings has an empty settings id."); continue; }
            if (!CheckText("A DefaultSettings settings id", group.Name, MaxSettingKeyLength, problems)) continue;
            var where = $"DefaultSettings {Quote(group.Name)}";
            if (group.Value.ValueKind != JsonValueKind.Object) { problems.Add($"{where} must be an object of setting names and values."); continue; }
            var props = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in group.Value.EnumerateObject())
            {
                if (props.Count >= MaxSettingsPerGroup) { problems.Add($"{where} sets more than {MaxSettingsPerGroup} values."); break; }
                if (!props.Add(prop.Name)) { problems.Add($"{where} sets {Quote(prop.Name)} more than once."); continue; }
                if (string.IsNullOrWhiteSpace(prop.Name)) { problems.Add($"{where} has an empty setting name."); continue; }
                if (!CheckText($"A setting name in {where}", prop.Name, MaxSettingKeyLength, problems)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) { problems.Add($"{where} value for {Quote(prop.Name)} must be text in double quotes (for example \"false\" or \"12\")."); continue; }
                CheckText($"{where} value for {Quote(prop.Name)}", prop.Value.GetString()!, MaxSettingValueLength, problems);
            }
        }
    }

    /// <summary>
    /// The first character that has no business in a record, or null. Control characters would corrupt a diff or a log;
    /// the invisible direction and formatting marks are refused because they can make reviewed text read differently
    /// from what it is.
    /// </summary>
    private static int? FindForbiddenChar(string s)
    {
        foreach (var c in s)
        {
            if (char.IsControl(c) || (char.IsSurrogate(c) && !IsPaired(s))) return c;
            // Zero-width and bidirectional marks, line/paragraph separators, word joiner, byte order mark.
            if (c is (>= (char)0x200B and <= (char)0x200F) or (>= (char)0x2028 and <= (char)0x202E) or (char)0x2060 or (>= (char)0x2066 and <= (char)0x2069) or (char)0xFEFF) return c;
        }
        return null;
    }

    private static bool IsPaired(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Submitted text made safe to show back in an issue comment: cut short, reduced to plain ASCII and wrapped as
    /// code, so it cannot carry markdown, HTML or a mention.
    /// </summary>
    public static string Quote(string s, int max = 60) => "`" + Printable(s, max) + "`";

    private static string Printable(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Length > max ? s[..max] : s)
            sb.Append(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || " ._:/-+*()[]{}=,#'\"".Contains(c) ? c : '?');
        if (s.Length > max) sb.Append("...");
        return sb.ToString();
    }

    // ---- validating the committed database ---------------------------------------------------------------------

    /// <summary>
    /// The same rules, applied to every record of a whole compat-db.json. CI runs this on the committed file so a
    /// hand-edited record is held to what a submitted one is.
    /// </summary>
    public static IReadOnlyList<string> ValidateDatabase(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, StrictJson); }
        catch (JsonException ex) { return [$"not valid JSON: {ex.Message}"]; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ["the file must be a JSON object with SchemaVersion and Records"];
            JsonElement? records = null;
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name == nameof(CompatDbFile.SchemaVersion))
                {
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var schema) || schema != CompatDb.CurrentSchemaVersion)
                        problems.Add($"SchemaVersion must be {CompatDb.CurrentSchemaVersion}");
                }
                else if (p.Name == nameof(CompatDbFile.Records)) records = p.Value;
                else problems.Add($"unknown top-level property '{p.Name}'");
            }
            if (records is not { ValueKind: JsonValueKind.Array }) { problems.Add("no Records list"); return problems; }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var record in records.Value.EnumerateArray())
            {
                index++;
                var id = record.ValueKind == JsonValueKind.Object && record.TryGetProperty(nameof(CompatRecord.Id), out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()! : "";
                var own = new List<string>();
                try { CheckRecord(record, own); }
                catch (InvalidOperationException) { own.Add("contains text that is not valid Unicode (a broken \\u escape)"); }
                if (id.Length > 0 && !ids.Add(id)) own.Add("there is more than one record with this Id (ids are compared without regard to case)");
                var label = id.Length > 0 ? $"record {index} ({id})" : $"record {index}";
                problems.AddRange(own.Select(p => $"{label}: {p}"));
            }
        }
        if (problems.Count > 0) return problems;

        try { CompatDb.Parse(json); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { problems.Add($"ModderLords could not read the file: {ex.Message}"); }
        return problems;
    }

    // ---- merging into the database -----------------------------------------------------------------------------

    /// <summary>
    /// The result of putting one record into a database text. <see cref="Previous"/> is the record it replaced (null:
    /// it is new); <see cref="Unchanged"/> means the database already said exactly this, so <see cref="Text"/> is the
    /// input untouched.
    /// </summary>
    public sealed record MergeResult(string Text, CompatRecord Record, CompatRecord? Previous, bool Unchanged)
    {
        public bool Added => Previous is null;
    }

    /// <summary>
    /// Puts <paramref name="record"/> into the database text by id, replacing a record with the same id (compared the
    /// way <see cref="CompatDb"/> compares them) or adding it, and stamps it with <paramref name="nowUtc"/>.
    ///
    /// The bundled file is hand-written: it is not in <see cref="CompatDb.Serialize"/>'s order, leaves out empty lists
    /// and keeps characters Serialize would escape. Rewriting it through Serialize would turn a one-record change into
    /// a whole-file diff nobody could review, so this splices text instead: the one record's lines come from Serialize,
    /// every other byte of the file stays as it was. A new record goes where Serialize's ordering puts it when the file
    /// is in that order, and at the end when it is not.
    ///
    /// <paramref name="keepStampFrom"/> is an earlier result for the same submission (the pull request branch's copy of
    /// the file). When it already holds this record, its UpdatedAt is kept, so running twice gives the same bytes.
    /// </summary>
    public static MergeResult Merge(string dbText, CompatRecord record, DateTime nowUtc, string? keepStampFrom = null)
    {
        var newline = dbText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var spans = RecordSpans(dbText);
        var existing = spans.FindIndex(s => s.Record.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        var previous = existing >= 0 ? spans[existing].Record : null;

        var next = record.Clone();
        if (previous is not null && SameIgnoringStamp(previous, next)) return new MergeResult(dbText, previous, previous, Unchanged: true);

        var now = DateTime.SpecifyKind(new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        next.UpdatedAt = now;
        if (keepStampFrom is not null)
        {
            try
            {
                var earlier = CompatDb.Parse(keepStampFrom).Records.FirstOrDefault(r => r.Id.Equals(next.Id, StringComparison.OrdinalIgnoreCase));
                if (earlier?.UpdatedAt is not null && SameIgnoringStamp(earlier, next)) next.UpdatedAt = earlier.UpdatedAt;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* an unreadable earlier copy just means a fresh stamp */ }
        }

        // One record exactly as Serialize writes it inside a file, so its indentation already matches a record's depth.
        var serialized = CompatDb.Serialize([next]);
        var own = RecordSpans(serialized).Single();
        var block = serialized[own.Start..own.End].Replace("\r\n", "\n").Replace("\n", newline);

        string merged;
        if (existing >= 0)
        {
            merged = dbText[..spans[existing].Start] + block + dbText[spans[existing].End..];
        }
        else if (spans.Count == 0)
        {
            merged = serialized.Replace("\r\n", "\n").Replace("\n", newline) + (dbText.EndsWith('\n') ? newline : "");
        }
        else
        {
            var sorted = spans.Zip(spans.Skip(1), (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Record.Id, b.Record.Id) <= 0).All(inOrder => inOrder);
            var before = sorted ? spans.FindIndex(s => StringComparer.OrdinalIgnoreCase.Compare(s.Record.Id, next.Id) > 0) : -1;
            if (before >= 0)
            {
                var at = spans[before].Start;
                merged = dbText[..at] + block + "," + newline + IndentBefore(dbText, at) + dbText[at..];
            }
            else
            {
                var last = spans[^1];
                merged = dbText[..last.End] + "," + newline + IndentBefore(dbText, last.Start) + block + dbText[last.End..];
            }
        }

        var after = ValidateDatabase(merged);
        if (after.Count > 0) throw new InvalidDataException("the merged database does not pass its own check: " + string.Join("; ", after));
        return new MergeResult(merged, next, previous, Unchanged: false);
    }

    private static bool SameIgnoringStamp(CompatRecord a, CompatRecord b)
    {
        var x = a.Clone(); var y = b.Clone();
        x.UpdatedAt = y.UpdatedAt = null;
        return CompatDb.Serialize([x]) == CompatDb.Serialize([y]);
    }

    /// <summary>The whitespace between the start of the line and <paramref name="index"/>, so an inserted record lines up with its neighbours.</summary>
    private static string IndentBefore(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var indent = text[lineStart..index];
        return indent.All(c => c is ' ' or '\t') ? indent : "";
    }

    private sealed record RecordSpan(int Start, int End, CompatRecord Record);

    /// <summary>Where each record's object sits in the text (character offsets), in file order.</summary>
    private static List<RecordSpan> RecordSpans(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        var byteSpans = new List<(int Start, int End)>();
        var inRecords = false;
        var start = -1;
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName) inRecords = reader.ValueTextEquals(nameof(CompatDbFile.Records));
            else if (inRecords && reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.StartObject) start = (int)reader.TokenStartIndex;
            else if (inRecords && reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.EndObject) byteSpans.Add((start, (int)reader.BytesConsumed));
        }

        var result = new List<RecordSpan>();
        foreach (var (s, e) in byteSpans)
        {
            var raw = Encoding.UTF8.GetString(bytes, s, e - s);
            var record = CompatDb.Parse("{\"SchemaVersion\":1,\"Records\":[" + raw + "]}").Records.Single();
            result.Add(new RecordSpan(Encoding.UTF8.GetCharCount(bytes, 0, s), Encoding.UTF8.GetCharCount(bytes, 0, e), record));
        }
        return result;
    }

    // ---- describing a change for the reviewer ------------------------------------------------------------------

    /// <summary>One field that differs between the record in the database and the submitted one. Values are JSON text; null: absent.</summary>
    public sealed record FieldChange(string Field, string? Before, string? After);

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Field-by-field difference, in the record's own property order. UpdatedAt is left out: it always changes.
    /// Values keep the serializer's default escaping, so anything outside plain ASCII shows up as a visible \u escape.
    /// </summary>
    public static IReadOnlyList<FieldChange> Describe(CompatRecord? previous, CompatRecord next)
    {
        var changes = new List<FieldChange>();
        foreach (var name in RecordProperties)
        {
            if (name == nameof(CompatRecord.UpdatedAt)) continue;
            var property = typeof(CompatRecord).GetProperty(name)!;
            var before = previous is null ? null : Display(property.GetValue(previous));
            var after = Display(property.GetValue(next));
            if (before != after) changes.Add(new FieldChange(name, before, after));
        }
        return changes;
    }

    /// <summary>A value as JSON for display, or null when it carries nothing (null, an empty list, an empty map).</summary>
    public static string? Display(object? value) => value switch
    {
        null => null,
        System.Collections.ICollection { Count: 0 } => null,
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, value.GetType(), DisplayJson).Replace("\r\n", "\n"),
    };
}
