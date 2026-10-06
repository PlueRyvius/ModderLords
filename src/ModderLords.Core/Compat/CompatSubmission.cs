using System.Text;
using System.Text.Json;
using ModderLords.Core.Updates;

namespace ModderLords.Core.Compat;

/// <summary>
/// The link Submit… opens. <see cref="RecordInUrl"/> is false when the record was too large to travel in the link:
/// the form then opens with <see cref="CompatSubmission.PastePlaceholder"/> in its record field and the caller has to
/// hand <see cref="RecordJson"/> to the user some other way (the app copies it to the clipboard).
/// </summary>
public sealed record CompatSubmissionLink(string Url, string RecordJson, bool RecordInUrl);

/// <summary>
/// Builds the prefilled GitHub issue that carries one compat record to the maintainer. Nothing is sent from here: the
/// link only opens the form in the user's browser, and the issue exists once they press Submit on GitHub.
///
/// PRIVACY: the link carries the record's own fields, the mod's version and Coop's version, and nothing else. The
/// issue is public and posted under the user's GitHub account, so nothing about their machine may be added here: no
/// paths, no profile or its name, no other mods, no account or machine name, no launcher diagnostics. Anything of that
/// kind belongs in the support bundle, which the user reviews and attaches themselves.
///
/// The field ids and the template name are a contract with .github/ISSUE_TEMPLATE/compat-record.yml and the workflow
/// that reads the issue; change them there and here together.
/// </summary>
public static class CompatSubmission
{
    public const string Template = "compat-record.yml";

    /// <summary>
    /// GitHub answers a longer address with an error page instead of the form (the limit is a little over 8,000
    /// characters and not documented), so a record that would cross this goes by clipboard instead.
    /// </summary>
    public const int MaxUrlLength = 8000;

    /// <summary>What the record field says when the record did not fit in the link.</summary>
    public const string PastePlaceholder = "Replace this line with the record: ModderLords copied it to your clipboard, so click here, select all and paste (Ctrl+V).";

    public static string TitleFor(CompatRecord record) => "Compat record: " + record.Id;

    // LF, not the platform's line ending: each line break costs three characters of the link once escaped, six as CRLF.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>
    /// One record as a JSON object, exactly as it appears inside an exported file. It is cut out of
    /// <see cref="CompatDb.Serialize"/>'s output rather than serialized again here, so the two cannot drift apart:
    /// wrapping this text in <c>{ "SchemaVersion": 1, "Records": [ ... ] }</c> always gives a file Import… accepts.
    /// </summary>
    public static string RecordJson(CompatRecord record)
    {
        using var doc = JsonDocument.Parse(CompatDb.Serialize([record]));
        return JsonSerializer.Serialize(doc.RootElement.GetProperty(nameof(CompatDbFile.Records))[0], Indented);
    }

    /// <summary>
    /// The link for one record. <paramref name="modVersion"/> and <paramref name="coopVersion"/> are the versions on
    /// the submitter's machine; a missing one leaves its field empty for them to fill in.
    /// </summary>
    public static CompatSubmissionLink Build(CompatRecord record, string? modVersion, string? coopVersion)
    {
        var json = RecordJson(record);
        var url = Url(record, modVersion, coopVersion, json);
        return url.Length <= MaxUrlLength
            ? new CompatSubmissionLink(url, json, RecordInUrl: true)
            : new CompatSubmissionLink(Url(record, modVersion, coopVersion, PastePlaceholder), json, RecordInUrl: false);
    }

    private static string Url(CompatRecord record, string? modVersion, string? coopVersion, string recordField)
    {
        var sb = new StringBuilder($"https://github.com/{UpdateChecker.Repository}/issues/new?template={Template}");
        Add(sb, "title", TitleFor(record));
        Add(sb, "mod", record.Id);
        Add(sb, "mod-version", modVersion);
        Add(sb, "coop-version", coopVersion);
        Add(sb, "record", recordField);
        return sb.ToString();
    }

    // EscapeDataString, never EscapeUriString or string concatenation: an id or a note containing & or # would
    // otherwise end the field early and the rest of the record would be read as other fields, or dropped.
    private static void Add(StringBuilder sb, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append('&').Append(field).Append('=').Append(Uri.EscapeDataString(value.Trim()));
    }
}
