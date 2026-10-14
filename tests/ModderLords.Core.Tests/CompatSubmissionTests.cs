using System.Text.Json;
using ModderLords.Core.Compat;
using ModderLords.Core.Overlay;
using ModderLords.Core.Updates;

namespace ModderLords.Core.Tests;

public sealed class CompatSubmissionTests
{
    private static CompatRecord Full(string id = "ImprovedGarrisons", string? notes = "n") => new()
    {
        Id = id, Verdict = CompatVerdict.NeedsRecipe, TestedVersions = ["v4.2.0.7"], TestedCoopVersion = "v0.1.4",
        DefaultRole = ServerRole.Run, ServerAuthoritative = true, ClientSideBehaviors = ["IG.UiBehavior"], KeepSubModules = ["x"],
        DefaultSettings = new() { ["IG"] = new() { ["Enabled"] = "true" } },
        Notes = notes, Url = "https://example/?a=1&b=2#frag", UpdatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>Reads the link the way GitHub will: split on the separators first, unescape each value after.</summary>
    private static Dictionary<string, string> Query(string url)
    {
        var q = url[(url.IndexOf('?') + 1)..];
        return q.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    [Fact]
    public void Link_FillsEveryFieldOfTheForm()
    {
        var link = CompatSubmission.Build(Full(), "v4.2.0.7", "v0.1.4");

        Assert.True(link.RecordInUrl);
        Assert.StartsWith($"https://github.com/{UpdateChecker.Repository}/issues/new?template=compat-record.yml&", link.Url);
        var q = Query(link.Url);
        Assert.Equal(new[] { "template", "title", "mod", "mod-version", "coop-version", "record" }, q.Keys.ToArray());   // and nothing else
        Assert.Equal("Compat record: ImprovedGarrisons", q["title"]);
        Assert.Equal("ImprovedGarrisons", q["mod"]);
        Assert.Equal("v4.2.0.7", q["mod-version"]);
        Assert.Equal("v0.1.4", q["coop-version"]);
        Assert.Equal(link.RecordJson, q["record"]);
    }

    [Fact]
    public void Link_LeavesAnUnknownVersionForTheUserToFillIn()
    {
        var q = Query(CompatSubmission.Build(Full(), null, " ").Url);
        Assert.False(q.ContainsKey("mod-version"));
        Assert.False(q.ContainsKey("coop-version"));
    }

    [Theory]
    [InlineData("Mod&Evil=1#x", "a & b = c # d + e ? f / g % 20")]
    [InlineData("Mod+Plus Space", "line one\r\nline two\n\ttabbed \"quoted\" \\back")]
    [InlineData("Módulo.Ünïcode", "работает · 動作します · \U0001F600")]
    public void Link_EscapesAwkwardIdsAndNotes(string id, string notes)
    {
        var link = CompatSubmission.Build(Full(id, notes), "v1.0+build&x", "v0.1#4");

        // Past the path, nothing may be left that a browser or GitHub reads as structure or re-encodes on the way.
        var query = link.Url[(link.Url.IndexOf('?') + 1)..];
        Assert.Matches("^[A-Za-z0-9%&=._~-]+$", query);
        Assert.Equal(6, query.Split('&').Length);

        var q = Query(link.Url);
        Assert.Equal(id, q["mod"]);
        Assert.Equal("Compat record: " + id, q["title"]);
        Assert.Equal("v1.0+build&x", q["mod-version"]);
        Assert.Equal("v0.1#4", q["coop-version"]);
        var parsed = CompatDb.Parse($"{{ \"SchemaVersion\": 1, \"Records\": [ {q["record"]} ] }}").Records.Single();
        Assert.Equal(id, parsed.Id);
        Assert.Equal(notes, parsed.Notes);
        Assert.Equal("https://example/?a=1&b=2#frag", parsed.Url);
    }

    [Fact]
    public void RecordJson_IsOneRecordObject_ThatImportAcceptsOnceWrapped()
    {
        var rec = Full();
        var json = CompatSubmission.RecordJson(rec);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal("ImprovedGarrisons", doc.RootElement.GetProperty("Id").GetString());
            Assert.Equal("NeedsRecipe", doc.RootElement.GetProperty("Verdict").GetString());   // enums as strings
            Assert.Equal("Run", doc.RootElement.GetProperty("DefaultRole").GetString());
            Assert.False(doc.RootElement.TryGetProperty("Records", out _));
        }

        var parsed = CompatDb.Parse($"{{ \"SchemaVersion\": 1, \"Records\": [ {json} ] }}").Records.Single();
        Assert.Equal(rec.Id, parsed.Id);
        Assert.Equal(rec.Verdict, parsed.Verdict);
        Assert.Equal(rec.TestedVersions, parsed.TestedVersions);
        Assert.Equal(rec.TestedCoopVersion, parsed.TestedCoopVersion);
        Assert.Equal(rec.DefaultRole, parsed.DefaultRole);
        Assert.Equal(rec.ServerAuthoritative, parsed.ServerAuthoritative);
        Assert.Equal(rec.ClientSideBehaviors, parsed.ClientSideBehaviors);
        Assert.Equal(rec.KeepSubModules, parsed.KeepSubModules);
        Assert.Equal("true", parsed.DefaultSettings["IG"]["Enabled"]);
        Assert.Equal(rec.Notes, parsed.Notes);
        Assert.Equal(rec.Url, parsed.Url);
        Assert.Equal(rec.UpdatedAt, parsed.UpdatedAt);
        // The same text an export of this one record holds, so a submitted record and an exported one never differ.
        Assert.Equal(CompatSubmission.RecordJson(parsed), json);
    }

    [Fact]
    public void RecordJson_OmitsNulls()
    {
        using var doc = JsonDocument.Parse(CompatSubmission.RecordJson(new CompatRecord { Id = "Bare", Verdict = CompatVerdict.Works }));
        foreach (var name in new[] { "TestedCoopVersion", "DefaultRole", "ServerAuthoritative", "ClientLoadsAfterCoop", "Notes", "Url", "UpdatedAt" })
            Assert.False(doc.RootElement.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void OversizedRecord_GoesByClipboard_AndTheLinkStillOpensTheForm()
    {
        var big = Full(notes: string.Join("\n", Enumerable.Repeat("crashes on the second day & again after a reload", 400)));
        var link = CompatSubmission.Build(big, "v4.2.0.7", "v0.1.4");

        Assert.False(link.RecordInUrl);
        Assert.True(link.Url.Length <= CompatSubmission.MaxUrlLength);
        var q = Query(link.Url);
        Assert.Equal("ImprovedGarrisons", q["mod"]);
        Assert.Equal("v4.2.0.7", q["mod-version"]);
        Assert.Equal("v0.1.4", q["coop-version"]);
        Assert.Equal(CompatSubmission.PastePlaceholder, q["record"]);
        // What the app puts on the clipboard is still the whole record.
        Assert.Equal(big.Notes, CompatDb.Parse($"{{ \"Records\": [ {link.RecordJson} ] }}").Records.Single().Notes);
    }

    [Fact]
    public void LengthGuard_CountsTheEscapedLink_NotTheJson()
    {
        // The JSON alone is under the limit, but each \uXXXX in it grows again in the link (%5C for the backslash).
        var link = CompatSubmission.Build(Full(notes: new string('я', 1000)), null, null);
        Assert.True(link.RecordJson.Length < CompatSubmission.MaxUrlLength);
        Assert.False(link.RecordInUrl);

        var fits = CompatSubmission.Build(Full(notes: new string('a', 1000)), null, null);
        Assert.True(fits.RecordInUrl);
        Assert.True(fits.Url.Length <= CompatSubmission.MaxUrlLength);
    }

    // ---- several records in one issue --------------------------------------------------------------------------

    private static CompatRecord Small(string id) => new() { Id = id, Verdict = CompatVerdict.Works, TestedVersions = ["v1.0.0"] };

    [Fact]
    public void SeveralRecords_TravelAsOneExportFile_TheIntakeAccepts()
    {
        var link = CompatSubmission.Build([Small("B.Mod"), Small("a mod")], "v0.1.6", "noted");
        var q = Query(link.Url);

        Assert.True(link.RecordInUrl);
        Assert.Equal("Compat records: 2 mods", q["title"]);
        Assert.Equal("B.Mod, a mod", q["mod"]);
        Assert.Equal("v0.1.6", q["coop-version"]);
        Assert.False(q.ContainsKey("mod-version"));            // each record carries its own in TestedVersions
        Assert.Equal(link.RecordJson, q["record"]);
        Assert.DoesNotContain("\r", q["record"]);
        // The same text Import… reads.
        Assert.Equal(["a mod", "B.Mod"], CompatDb.Parse(q["record"]).Records.Select(r => r.Id));
    }

    [Fact]
    public void SeveralRecords_LoseTheirWhitespaceBeforeTheyLoseTheLink()
    {
        // Enough records that the readable form is too long for a link and the compact one is not.
        var records = Enumerable.Range(0, 30).Select(i => Small($"Mod{i:00}")).ToList();
        var count = records.Count;
        while (count > 1 && Uri.EscapeDataString(CompatSubmission.RecordsJson(records.Take(count).ToList(), compact: true)).Length > CompatSubmission.MaxUrlLength - 600) count--;
        var fitting = records.Take(count).ToList();
        Assert.True(Uri.EscapeDataString(CompatSubmission.RecordsJson(fitting)).Length > CompatSubmission.MaxUrlLength, "the fixture should not fit readable");

        var link = CompatSubmission.Build(fitting, "v0.1.6");

        Assert.True(link.RecordInUrl);
        Assert.True(link.Url.Length <= CompatSubmission.MaxUrlLength);
        Assert.DoesNotContain("\n", link.RecordJson);
        Assert.Equal(count, CompatDb.Parse(Query(link.Url)["record"]).Records.Count);
    }

    [Fact]
    public void SeveralRecords_TooLargeEvenCompact_GoByClipboard_Readable()
    {
        var records = Enumerable.Range(0, 30).Select(i => new CompatRecord { Id = $"Mod{i:00}", Notes = new string('n', 400) }).ToList();

        var link = CompatSubmission.Build(records, null);

        Assert.False(link.RecordInUrl);
        Assert.True(link.Url.Length <= CompatSubmission.MaxUrlLength);
        Assert.Equal(CompatSubmission.PastePlaceholder, Query(link.Url)["record"]);
        Assert.Contains("\n", link.RecordJson);
        Assert.Equal(30, CompatDb.Parse(link.RecordJson).Records.Count);
    }

    [Fact]
    public void ProofNote_ForSeveral_SaysWhichWereWatched_AndClaimsNothingWhenNoneWere()
    {
        var day = new DateTime(2026, 10, 6, 23, 59, 0, DateTimeKind.Utc);
        var note = CompatSubmission.ProofNote([("A", day), ("B", null), ("C", day)])!;

        Assert.Contains("- A: 2026-10-06", note);
        Assert.Contains("- C: 2026-10-06", note);
        Assert.Contains("Not watched by ModderLords (records written by hand): B", note);
        Assert.Null(CompatSubmission.ProofNote([("A", null), ("B", null)]));
    }
}
