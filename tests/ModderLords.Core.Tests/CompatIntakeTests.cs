using System.Text.Json;
using ModderLords.Core.Compat;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// The gate a submitted compatibility record passes before it can reach compat-db.json: reading the issue form,
/// refusing anything that is not one well-formed record, and merging without disturbing the rest of the file.
/// </summary>
public sealed class CompatIntakeTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 30, 45, 678, DateTimeKind.Utc);

    // Raw literals take the source file's line endings, which depend on the checkout; the tests want one known form.
    private static readonly string GoodRecord = """
        {
          "Id": "SomeMod",
          "Verdict": "NeedsRecipe",
          "TestedVersions": ["v1.2.3"],
          "TestedCoopVersion": "v0.1.5",
          "DefaultRole": "DependencyOnly",
          "ServerAuthoritative": true,
          "KeepSubModules": ["SomeMod.SubModule"],
          "EnsureLines": [ { "File": "config/coop-modules.txt", "Section": "modules", "Value": "{coopModuleId}" } ],
          "DefaultSettings": { "SomeMod": { "EnableThing": "false" } },
          "Notes": "Reaches SERVING with the thing turned off.",
          "Url": "https://www.nexusmods.com/mountandblade2bannerlord/mods/1"
        }
        """.ReplaceLineEndings("\n");

    private static string Body(string record, string mod = "SomeMod", string newline = "\n", bool fence = true) =>
        string.Join(newline,
            "### Module id", "", mod, "",
            "### Mod version", "", "v1.2.3", "",
            "### Coop version", "", "_No response_", "",
            "### Compatibility record", "",
            fence ? "```json" + newline + record.Replace("\n", newline) + newline + "```" : record.Replace("\n", newline), "",
            "### Notes", "", "Hosted a two-player campaign for an hour.", "");

    /// <summary>A record with one property replaced or added, so each rejection test changes exactly one thing.</summary>
    private static string With(string property, string valueJson)
    {
        var fields = JsonDocument.Parse(GoodRecord).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        fields[property] = valueJson;
        return "{" + string.Join(",", fields.Select(f => JsonSerializer.Serialize(f.Key) + ":" + f.Value)) + "}";
    }

    private static string Refusal(string json, string? expectedId = "SomeMod")
    {
        var result = CompatIntake.ValidateSubmission(json, expectedId);
        Assert.False(result.IsValid, "expected the record to be refused");
        Assert.Null(result.Record);
        return string.Join(" | ", result.Problems);
    }

    // ---- reading the issue body --------------------------------------------------------------------------------

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Body_FieldsAreRead_WithEitherLineEnding(string newline)
    {
        var problems = new List<string>();
        var form = CompatIntake.ParseIssueBody(Body(GoodRecord, newline: newline), problems);

        Assert.Empty(problems);
        Assert.Equal("SomeMod", form.Mod);
        Assert.Equal("v1.2.3", form.ModVersion);
        Assert.Null(form.CoopVersion);                         // "_No response_" is an empty field
        Assert.Equal("Hosted a two-player campaign for an hour.", form.Notes);
        Assert.StartsWith("{", form.Record);
        Assert.EndsWith("}", form.Record);
        Assert.True(CompatIntake.ValidateIssue(Body(GoodRecord, newline: newline)).IsValid);
    }

    [Fact]
    public void Body_WithoutAFence_IsStillRead()
    {
        var result = CompatIntake.ValidateIssue(Body(GoodRecord, fence: false));
        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
        Assert.Equal("SomeMod", result.Record!.Id);
    }

    [Fact]
    public void Body_HeadingInsideTheRecord_DoesNotReplaceTheRealField()
    {
        // A forged "### Module id" inside the fence must stay record text (and then fail as JSON), not become the field.
        var forged = GoodRecord + "\n### Module id\n\nOtherMod";
        var problems = new List<string>();
        var form = CompatIntake.ParseIssueBody(Body(forged), problems);

        Assert.Empty(problems);
        Assert.Equal("SomeMod", form.Mod);
        Assert.Contains("OtherMod", form.Record);
        Assert.False(CompatIntake.ValidateIssue(Body(forged)).IsValid);
    }

    [Fact]
    public void Body_RepeatedHeading_IsRefused()
    {
        var body = Body(GoodRecord) + "\n### Module id\n\nOtherMod\n";
        var result = CompatIntake.ValidateIssue(body);
        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("more than once"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just some text, not the form.")]
    [InlineData("### Module id\n\nSomeMod\n\n### Compatibility record\n\n_No response_\n")]
    public void Body_MissingFields_AreNamed(string body)
    {
        var result = CompatIntake.ValidateIssue(body);
        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains(CompatIntake.RecordLabel));
    }

    [Fact]
    public void Body_VersionFields_AreOnlyShownBackWhenPlain()
    {
        Assert.Equal("v1.2.3", CompatIntake.SafeVersion(" v1.2.3 "));
        Assert.Null(CompatIntake.SafeVersion("v1 <img src=x>"));
        Assert.Null(CompatIntake.SafeVersion("@someone"));
        Assert.Null(CompatIntake.SafeVersion(null));
    }

    // ---- accepting -----------------------------------------------------------------------------------------------

    [Fact]
    public void ValidRecord_IsAccepted_AndReadByTheLaunchersOwnParser()
    {
        var result = CompatIntake.ValidateSubmission(GoodRecord, "SomeMod");

        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
        var r = result.Record!;
        Assert.Equal(CompatVerdict.NeedsRecipe, r.Verdict);
        Assert.Equal(ServerRole.DependencyOnly, r.DefaultRole);
        Assert.Equal("config/coop-modules.txt", Assert.Single(r.EnsureLines).File);
        Assert.Equal("false", r.DefaultSettings["SomeMod"]["EnableThing"]);
    }

    [Fact]
    public void ExportFileShape_WithOneRecord_IsAccepted()
    {
        var result = CompatIntake.ValidateSubmission("{ \"SchemaVersion\": 1, \"Records\": [ " + GoodRecord + " ] }", "SomeMod");
        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
        Assert.Equal("SomeMod", result.Record!.Id);
    }

    [Fact]
    public void WhatTheLauncherExports_IsAccepted()
    {
        // Serialize writes every list, empty or not, and a full timestamp; a submission is exactly that text.
        var exported = CompatDb.Serialize([new CompatRecord { Id = "SomeMod", Verdict = CompatVerdict.Works, UpdatedAt = Now }]);
        var result = CompatIntake.ValidateSubmission(exported, "SomeMod");
        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
    }

    // ---- refusing ------------------------------------------------------------------------------------------------

    [Fact]
    public void NotExactlyOneRecord_IsRefused()
    {
        Assert.Contains("2 records", Refusal("{ \"SchemaVersion\": 1, \"Records\": [ " + GoodRecord + ", " + GoodRecord + " ] }"));
        Assert.Contains("0 records", Refusal("{ \"SchemaVersion\": 1, \"Records\": [] }"));
        Assert.Contains("must be a JSON object", Refusal("[ " + GoodRecord + " ]"));
        Assert.Contains("SchemaVersion must be", Refusal("{ \"SchemaVersion\": 2, \"Records\": [ " + GoodRecord + " ] }"));
        Assert.Contains("unknown property", Refusal("{ \"SchemaVersion\": 1, \"Extra\": 1, \"Records\": [ " + GoodRecord + " ] }"));
    }

    [Theory]
    [InlineData("\"\"", "Id is empty")]
    [InlineData("\"   \"", "Id is empty")]
    [InlineData("null", "Id is empty")]
    [InlineData("\"Some Mod\"", "not a module id")]
    [InlineData("\"Some`Mod\"", "not a module id")]
    [InlineData("\"../SomeMod\"", "not a module id")]
    public void BadId_IsRefused(string idJson, string expected) => Assert.Contains(expected, Refusal(With("Id", idJson), expectedId: null));

    [Fact]
    public void MissingId_IsRefused() => Assert.Contains("has no Id", Refusal("{ \"Verdict\": \"Works\" }", expectedId: null));

    [Theory]
    [InlineData("OtherMod")]
    [InlineData("somemod")] // the database compares ids without case, but a submission must name its mod exactly
    public void IdThatDoesNotMatchTheModField_IsRefused(string modField) =>
        Assert.Contains("must be the same mod", Refusal(GoodRecord, expectedId: modField));

    [Theory]
    [InlineData("Verdict", "\"Wokrs\"")]
    [InlineData("Verdict", "\"works\"")]
    [InlineData("Verdict", "1")]       // the launcher's converter would take a number
    [InlineData("Verdict", "99")]
    [InlineData("Verdict", "null")]
    [InlineData("DefaultRole", "\"Client\"")]
    [InlineData("DefaultRole", "2")]
    public void UnknownEnumValue_IsRefused(string property, string valueJson) =>
        Assert.Contains($"{property} is", Refusal(With(property, valueJson)));

    [Theory]
    [InlineData("Verdikt")]
    [InlineData("verdict")]            // a typo in case is still a typo: the launcher would read it, a strict gate must not guess
    [InlineData("EnsureLine")]
    public void UnknownProperty_IsRefused_NotDropped(string property) =>
        Assert.Contains("Unknown property", Refusal(With(property, "\"Works\"")));

    [Fact]
    public void RepeatedProperty_IsRefused() =>
        Assert.Contains("more than once", Refusal("{ \"Id\": \"SomeMod\", \"Verdict\": \"Works\", \"Verdict\": \"Broken\" }"));

    [Fact]
    public void OversizedInput_IsRefused()
    {
        Assert.Contains("larger than 32 KB", Refusal(With("Notes", JsonSerializer.Serialize(new string('a', CompatIntake.MaxRecordBytes)))));
        Assert.Contains("Notes is", Refusal(With("Notes", JsonSerializer.Serialize(new string('a', CompatIntake.MaxNotesLength + 1)))));
        Assert.Contains("the limit is", Refusal(With("TestedVersions", JsonSerializer.Serialize(Enumerable.Repeat("v1", CompatIntake.MaxTestedVersions + 1)))));
        Assert.Contains("the limit is", Refusal(With("KeepSubModules", JsonSerializer.Serialize(Enumerable.Repeat("A.B", CompatIntake.MaxListItems + 1)))));
        Assert.Contains("characters long", Refusal(With("KeepSubModules", JsonSerializer.Serialize(new[] { new string('a', CompatIntake.MaxTypeNameLength + 1) }))));
        Assert.Contains("characters long", Refusal(With("TestedCoopVersion", JsonSerializer.Serialize(new string('1', CompatIntake.MaxVersionLength + 1)))));
    }

    [Theory]
    [InlineData("Notes", "\"line one\\nline two\"")]
    [InlineData("Notes", "\"tab\\there\"")]
    [InlineData("Notes", "\"nul\\u0000\"")]
    [InlineData("Notes", "\"reversed \\u202E text\"")]     // right-to-left override: reviewed text would read differently
    [InlineData("Notes", "\"zero\\u200Bwidth\"")]
    [InlineData("TestedCoopVersion", "\"v0.1.5\\r\"")]
    [InlineData("KeepSubModules", "[\"A.B\\u0007\"]")]
    [InlineData("DefaultSettings", "{ \"Some\\u0001Mod\": { \"A\": \"1\" } }")]
    [InlineData("DefaultSettings", "{ \"SomeMod\": { \"A\": \"1\\u001B[0m\" } }")]
    public void ControlAndInvisibleCharacters_AreRefused(string property, string valueJson) =>
        Assert.Contains("control or invisible", Refusal(With(property, valueJson)));

    [Theory]
    [InlineData("ftp://example.com/mod")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/system32")]
    [InlineData("www.nexusmods.com/mods/1")]
    [InlineData("https://nexusmods.com@evil.example/mods/1")]
    [InlineData("https://example.com/a b")]
    [InlineData("HTTPS://")]
    public void UrlThatIsNotPlainHttp_IsRefused(string url) =>
        Assert.Contains("Url must be", Refusal(With("Url", JsonSerializer.Serialize(url))));

    [Theory]
    [InlineData("C:\\\\Windows\\\\win.ini", "relative to the mod's folder")]
    [InlineData("/etc/passwd.txt", "relative to the mod's folder")]
    [InlineData("\\\\\\\\server\\\\share\\\\a.txt", "relative to the mod's folder")]
    [InlineData("../OtherMod/config.txt", "must stay inside")]
    [InlineData("config/../../OtherMod/config.txt", "must stay inside")]
    [InlineData("config\\\\..\\\\..\\\\a.txt", "must stay inside")]
    [InlineData("config//a.txt", "empty path segment")]
    [InlineData("config/a.txt/", "empty path segment")]
    [InlineData("config/a.txt:stream", "relative to the mod's folder")]
    [InlineData("config/%2e%2e/a.txt", "may only use")]
    [InlineData("config/a.txt.", "ends with a space or dot")]
    [InlineData("NUL.txt", "reserved Windows device name")]
    [InlineData("bin/Win64_Shipping_Client/SomeMod.dll", "not a line-based config file")]
    [InlineData("SubModule.xml", "not a line-based config file")]
    [InlineData("config", "not a line-based config file")]
    [InlineData("", "is empty")]
    public void EnsureLines_FileOutsideTheModFolderOrNotAConfigFile_IsRefused(string fileJsonBody, string expected) =>
        Assert.Contains(expected, Refusal(With("EnsureLines", "[ { \"File\": \"" + fileJsonBody + "\", \"Section\": null, \"Value\": \"x\" } ]")));

    [Theory]
    [InlineData("{ \"File\": \"a.txt\" }", "has no Value")]
    [InlineData("{ \"Value\": \"x\" }", "has no File")]
    [InlineData("{ \"File\": \"a.txt\", \"Value\": \"   \" }", "Value is empty")]
    [InlineData("{ \"File\": \"a.txt\", \"Value\": \"{serverPath}\" }", "is not the {coopModuleId} token")]
    [InlineData("{ \"File\": \"a.txt\", \"Value\": \"x\", \"Section\": \"[modules]\" }", "without brackets")]
    [InlineData("{ \"File\": \"a.txt\", \"Value\": \"x\", \"Mode\": \"overwrite\" }", "unknown property")]
    [InlineData("{ \"File\": \"a.txt\", \"Value\": \"x\\ny\" }", "control or invisible")]
    [InlineData("\"a.txt\"", "must be an object")]
    public void EnsureLines_MalformedEntry_IsRefused(string entryJson, string expected) =>
        Assert.Contains(expected, Refusal(With("EnsureLines", "[ " + entryJson + " ]")));

    [Fact]
    public void EnsureLines_TooMany_IsRefused()
    {
        var entries = string.Join(",", Enumerable.Repeat("{ \"File\": \"a.txt\", \"Value\": \"x\" }", CompatIntake.MaxEnsureLines + 1));
        Assert.Contains("the limit is", Refusal(With("EnsureLines", "[" + entries + "]")));
    }

    [Theory]
    [InlineData("{ \"SomeMod\": { \"EnableThing\": false } }", "must be text in double quotes")]
    [InlineData("{ \"SomeMod\": { \"Count\": 3 } }", "must be text in double quotes")]
    [InlineData("{ \"SomeMod\": \"false\" }", "must be an object")]
    [InlineData("{ \"\": { \"A\": \"1\" } }", "empty settings id")]
    [InlineData("{ \"SomeMod\": { \"\": \"1\" } }", "empty setting name")]
    [InlineData("{ \"SomeMod\": { \"A\": \"1\", \"A\": \"2\" } }", "more than once")]
    [InlineData("[]", "must be an object")]
    public void DefaultSettings_Malformed_IsRefused(string valueJson, string expected) =>
        Assert.Contains(expected, Refusal(With("DefaultSettings", valueJson)));

    [Fact]
    public void DefaultSettings_TooLarge_IsRefused()
    {
        var manyValues = "{ \"SomeMod\": {" + string.Join(",", Enumerable.Range(0, CompatIntake.MaxSettingsPerGroup + 1).Select(i => $"\"P{i}\":\"1\"")) + "} }";
        Assert.Contains("sets more than", Refusal(With("DefaultSettings", manyValues)));
        var manyGroups = "{" + string.Join(",", Enumerable.Range(0, CompatIntake.MaxSettingsGroups + 1).Select(i => $"\"G{i}\":{{}}")) + "}";
        Assert.Contains("more than", Refusal(With("DefaultSettings", manyGroups)));
        Assert.Contains("characters long", Refusal(With("DefaultSettings", "{ \"SomeMod\": { \"A\": " + JsonSerializer.Serialize(new string('x', CompatIntake.MaxSettingValueLength + 1)) + " } }")));
    }

    [Theory]
    [InlineData("TestedVersions", "null", "must be a list")]
    [InlineData("TestedVersions", "\"v1\"", "must be a list")]
    [InlineData("TestedVersions", "[1]", "must be text")]
    [InlineData("TestedVersions", "[\"\"]", "is empty")]
    [InlineData("ServerAuthoritative", "\"yes\"", "must be true, false or null")]
    [InlineData("ClientLoadsAfterCoop", "1", "must be true, false or null")]
    [InlineData("Notes", "5", "must be text")]
    [InlineData("UpdatedAt", "\"yesterday\"", "UpdatedAt must be")]
    [InlineData("UpdatedAt", "20260902", "UpdatedAt must be")]
    public void WrongValueType_IsRefused(string property, string valueJson, string expected) =>
        Assert.Contains(expected, Refusal(With(property, valueJson)));

    [Theory]
    [InlineData("{ \"Id\": \"SomeMod\", }")]              // trailing comma
    [InlineData("{ \"Id\": \"SomeMod\" } // done")]       // comment
    [InlineData("{ \"Id\": \"SomeMod\"")]
    [InlineData("not json")]
    public void BrokenJson_IsRefused(string json) => Assert.Contains("not valid JSON", Refusal(json));

    [Fact]
    public void Refusals_NeverEchoSubmittedMarkup()
    {
        // What comes back goes into a public issue comment written by a bot.
        var text = Refusal(With("<img src=x onerror=alert(1)> @maintainer [x](https://evil.example)", "1"));
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("@maintainer", text);
        Assert.Contains("Unknown property", text);
    }

    [Fact]
    public void EveryRecordProperty_HasARule()
    {
        // Adding a property to CompatRecord without deciding how a submission of it is checked must fail here.
        var actual = typeof(CompatRecord).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(actual, CompatIntake.RecordProperties.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in CompatIntake.RecordProperties)
            Assert.DoesNotContain("Unknown property", string.Join(" | ", CompatIntake.ValidateSubmission(With(name, "null"), "SomeMod").Problems));
        Assert.Equal(new[] { "File", "Section", "Value" }, typeof(EnsureLine).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---- merging -------------------------------------------------------------------------------------------------

    // Hand-written like the bundled file: not in id order, empty lists left out, characters Serialize would escape.
    private static readonly string HandWrittenDb = """
        {
          "SchemaVersion": 1,
          "Records": [
            {
              "Id": "Zeta",
              "Verdict": "Works",
              "Notes": "It's fine <really>.",
              "UpdatedAt": "2026-09-02T00:00:00Z"
            },
            {
              "Id": "SomeMod",
              "Verdict": "Broken",
              "DefaultRole": "Run",
              "UpdatedAt": "2026-09-03T00:00:00Z"
            },
            {
              "Id": "Alpha",
              "Verdict": "Unknown",
              "UpdatedAt": "2026-09-04T00:00:00Z"
            }
          ]
        }

        """.ReplaceLineEndings("\n");

    private static CompatRecord Submitted(string? json = null) => CompatIntake.ValidateSubmission(json ?? GoodRecord, null).Record!;

    private static string RecordText(string db, string id)
    {
        var start = db.IndexOf("\"Id\": \"" + id + "\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no record {id}");
        start = db.LastIndexOf('{', start);
        var depth = 0;
        for (var i = start; i < db.Length; i++)
        {
            if (db[i] == '{') depth++;
            else if (db[i] == '}' && --depth == 0) return db[start..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced");
    }

    [Fact]
    public void Merge_Replace_TouchesOnlyThatRecord()
    {
        var result = CompatIntake.Merge(HandWrittenDb, Submitted(), Now);

        Assert.False(result.Added);
        Assert.False(result.Unchanged);
        Assert.Equal(CompatVerdict.Broken, result.Previous!.Verdict);
        // The neighbours are byte for byte what they were, including text Serialize would have escaped.
        Assert.Equal(RecordText(HandWrittenDb, "Zeta"), RecordText(result.Text, "Zeta"));
        Assert.Equal(RecordText(HandWrittenDb, "Alpha"), RecordText(result.Text, "Alpha"));
        Assert.Contains("\"Notes\": \"It's fine <really>.\"", result.Text);
        // Everything outside the replaced record is unchanged: cutting it out of both leaves the same text.
        Assert.Equal(HandWrittenDb.Replace(RecordText(HandWrittenDb, "SomeMod"), "#"), result.Text.Replace(RecordText(result.Text, "SomeMod"), "#"));

        var merged = CompatDb.Parse(result.Text).Records;
        Assert.Equal(new[] { "Zeta", "SomeMod", "Alpha" }, merged.Select(r => r.Id));
        Assert.Equal(CompatVerdict.NeedsRecipe, merged[1].Verdict);
        Assert.Equal(ServerRole.DependencyOnly, merged[1].DefaultRole);
        Assert.Empty(CompatIntake.ValidateDatabase(result.Text));
    }

    [Fact]
    public void Merge_Replace_MatchesIdWithoutCase_AndTakesTheSubmittedSpelling()
    {
        var result = CompatIntake.Merge(HandWrittenDb, Submitted(With("Id", "\"somemod\"")), Now);
        Assert.False(result.Added);
        Assert.Equal(new[] { "Zeta", "somemod", "Alpha" }, CompatDb.Parse(result.Text).Records.Select(r => r.Id));
    }

    [Fact]
    public void Merge_StampsUpdatedAtInUtc_ToTheSecond()
    {
        var stale = With("UpdatedAt", "\"2020-01-01T00:00:00Z\"");   // whatever the submission claims is replaced
        var result = CompatIntake.Merge(HandWrittenDb, Submitted(stale), Now);

        Assert.Contains("\"UpdatedAt\": \"2026-10-05T12:30:45Z\"", RecordText(result.Text, "SomeMod"));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 30, 45, DateTimeKind.Utc), result.Record.UpdatedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void Merge_Add_ToAFileThatIsNotInOrder_GoesAtTheEnd()
    {
        var result = CompatIntake.Merge(HandWrittenDb, Submitted(With("Id", "\"Middle\"")), Now);

        Assert.True(result.Added);
        Assert.Null(result.Previous);
        Assert.Equal(new[] { "Zeta", "SomeMod", "Alpha", "Middle" }, CompatDb.Parse(result.Text).Records.Select(r => r.Id));
        Assert.StartsWith(HandWrittenDb[..HandWrittenDb.LastIndexOf("    }", StringComparison.Ordinal)], result.Text);   // nothing before it moved
        Assert.Contains("    },\n    {\n      \"Id\": \"Middle\",", result.Text);                                       // and it lines up
        Assert.EndsWith("    }\n  ]\n}\n", result.Text);
    }

    [Theory]
    [InlineData("Aardvark", new[] { "Aardvark", "alpha", "SomeMod", "Zeta" })]
    [InlineData("Middle", new[] { "alpha", "Middle", "SomeMod", "Zeta" })]
    [InlineData("zzz", new[] { "alpha", "SomeMod", "Zeta", "zzz" })]
    public void Merge_Add_ToAFileInSerializeOrder_KeepsThatOrder(string id, string[] expected)
    {
        var sorted = CompatDb.Serialize([new CompatRecord { Id = "Zeta" }, new CompatRecord { Id = "SomeMod" }, new CompatRecord { Id = "alpha" }]);
        var result = CompatIntake.Merge(sorted, Submitted(With("Id", JsonSerializer.Serialize(id))), Now);

        Assert.Equal(expected, CompatDb.Parse(result.Text).Records.Select(r => r.Id));
        // A file that was Serialize's own output still is: the spliced record is indistinguishable from a rewrite.
        Assert.Equal(CompatDb.Serialize(CompatDb.Parse(result.Text).Records), result.Text);
    }

    [Fact]
    public void Merge_IntoAnEmptyDatabase_Works()
    {
        var result = CompatIntake.Merge("{\n  \"SchemaVersion\": 1,\n  \"Records\": []\n}\n", Submitted(), Now);
        Assert.True(result.Added);
        Assert.Equal("SomeMod", Assert.Single(CompatDb.Parse(result.Text).Records).Id);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Merge_KeepsTheFilesLineEndings(string newline)
    {
        var db = HandWrittenDb.Replace("\r\n", "\n").Replace("\n", newline);
        foreach (var record in new[] { Submitted(), Submitted(With("Id", "\"Middle\"")) })
        {
            var text = CompatIntake.Merge(db, record, Now).Text;
            Assert.Equal(-1, text.Replace(newline, "").IndexOfAny(['\r', '\n']));   // no line ending of the other kind
        }
    }

    [Fact]
    public void Merge_SameRecordAgain_IsReportedUnchanged_AndLeavesTheTextAlone()
    {
        var first = CompatIntake.Merge(HandWrittenDb, Submitted(), Now);
        var again = CompatIntake.Merge(first.Text, Submitted(), Now.AddDays(1));

        Assert.True(again.Unchanged);
        Assert.Same(first.Text, again.Text);
    }

    [Fact]
    public void Merge_ReRunOfTheSameSubmission_GivesTheSameBytes()
    {
        // The pull request branch already holds the first run's result; a second run (the form fires two events when
        // it is submitted) must not move UpdatedAt and so must not produce a new commit.
        var first = CompatIntake.Merge(HandWrittenDb, Submitted(), Now);
        var second = CompatIntake.Merge(HandWrittenDb, Submitted(), Now.AddMinutes(5), keepStampFrom: first.Text);
        Assert.Equal(first.Text, second.Text);

        // ...but an edited submission gets a fresh stamp.
        var edited = CompatIntake.Merge(HandWrittenDb, Submitted(With("Verdict", "\"Works\"")), Now.AddMinutes(5), keepStampFrom: first.Text);
        Assert.Contains("2026-10-05T12:35:45Z", edited.Text);
    }

    [Fact]
    public void Describe_ListsOnlyTheFieldsThatDiffer()
    {
        var result = CompatIntake.Merge(HandWrittenDb, Submitted(), Now);
        var changes = CompatIntake.Describe(result.Previous, result.Record);

        Assert.DoesNotContain(changes, c => c.Field is "Id" or "UpdatedAt");
        var verdict = Assert.Single(changes, c => c.Field == "Verdict");
        Assert.Equal("\"Broken\"", verdict.Before);
        Assert.Equal("\"NeedsRecipe\"", verdict.After);
        var ensure = Assert.Single(changes, c => c.Field == "EnsureLines");
        Assert.Null(ensure.Before);                                   // an empty list reads as "nothing"
        Assert.Contains("coop-modules.txt", ensure.After);

        var added = CompatIntake.Describe(null, result.Record);
        Assert.All(added, c => Assert.Null(c.Before));
        Assert.Contains(added, c => c.Field == "Id");
    }

    // ---- the committed database and the committed form ----------------------------------------------------------

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ModderLords.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root from " + AppContext.BaseDirectory);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void CommittedDatabase_PassesTheSameChecksAsASubmission() =>
        Assert.Empty(CompatIntake.ValidateDatabase(File.ReadAllText(RepoFile("src", "ModderLords.Core", "compat-db.json"))));

    [Fact]
    public void Database_HandEditedBadly_IsCaught()
    {
        var typo = HandWrittenDb.Replace("\"DefaultRole\": \"Run\"", "\"DefaultRol\": \"Run\"");
        Assert.Contains(CompatIntake.ValidateDatabase(typo), p => p.Contains("record 2 (SomeMod)") && p.Contains("Unknown property"));

        var duplicate = HandWrittenDb.Replace("\"Id\": \"Alpha\"", "\"Id\": \"zeta\"");
        Assert.Contains(CompatIntake.ValidateDatabase(duplicate), p => p.Contains("more than one record"));

        var escaping = HandWrittenDb.Replace("\"DefaultRole\": \"Run\"", "\"EnsureLines\": [ { \"File\": \"../x.txt\", \"Value\": \"y\" } ]");
        Assert.Contains(CompatIntake.ValidateDatabase(escaping), p => p.Contains("must stay inside"));

        Assert.NotEmpty(CompatIntake.ValidateDatabase(HandWrittenDb.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 1, \"Extra\": true")));
        Assert.NotEmpty(CompatIntake.ValidateDatabase("[]"));
        Assert.Empty(CompatIntake.ValidateDatabase(HandWrittenDb));
    }

    [Fact]
    public void IssueForm_UsesTheLabelsAndIdsTheParserAndTheAppRelyOn()
    {
        // GitHub renders the labels as headings; the app builds a prefilled URL from the ids. Both are a contract.
        var form = File.ReadAllText(RepoFile(".github", "ISSUE_TEMPLATE", "compat-record.yml")).Replace("\r\n", "\n");
        var expected = new (string Id, string Label)[]
        {
            ("mod", CompatIntake.ModLabel), ("mod-version", CompatIntake.ModVersionLabel), ("coop-version", CompatIntake.CoopVersionLabel),
            ("record", CompatIntake.RecordLabel), ("notes", CompatIntake.NotesLabel),
        };
        var at = 0;
        foreach (var (id, label) in expected)
        {
            var idAt = form.IndexOf($"    id: {id}\n", at, StringComparison.Ordinal);
            Assert.True(idAt >= 0, $"field id '{id}' is missing or out of order");
            var labelAt = form.IndexOf($"      label: {label}\n", idAt, StringComparison.Ordinal);
            Assert.True(labelAt >= 0, $"field '{id}' should have the label '{label}'");
            at = labelAt;
        }
        Assert.Contains("title: \"Compat record: \"", form);
        Assert.Contains("labels: [\"compat-record\"]", form);
        Assert.Contains("      render: json\n", form);
    }
}
