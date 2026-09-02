using ModularCoop.Core.Compat;
using ModularCoop.Core.Overlay;
using ModularCoop.Core.Profiles;

namespace ModularCoop.Core.Tests;

public sealed class CompatDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mbc-compat-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_dir, "compat-db.json");
    private string Local => Path.Combine(_dir, "compat-db.local.json");

    public CompatDbTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static CompatRecord Rec(string id, CompatVerdict v, params string[] versions) =>
        new() { Id = id, Verdict = v, TestedVersions = versions.ToList() };

    [Fact]
    public void NoFiles_FallsBackToOldTables()
    {
        var db = CompatDb.Load(Bundled, Local);
        Assert.Empty(db.Problems);
        Assert.Equal(ServerRole.DependencyOnly, db.DefaultRoleFor("Bannerlord.Harmony"));
        Assert.Equal(ServerRole.DependencyOnly, db.DefaultRoleFor("bannerlord.mboptionscreen"));
        Assert.Equal(ServerRole.Run, db.DefaultRoleFor("SomethingElse"));
        Assert.Equal(CompatDb.FallbackKeepSubModules.OrderBy(s => s), db.KeepForDependencyOnly().OrderBy(s => s));
        Assert.Equal(CompatBadge.None, db.For("SomethingElse", "v1.0.0"));
    }

    [Fact]
    public void LocalRecord_WinsWholeRecord()
    {
        CompatDb.WriteFile(Bundled, [new CompatRecord { Id = "ModA", Verdict = CompatVerdict.Works, Notes = "bundled", DefaultRole = ServerRole.DependencyOnly }]);
        CompatDb.WriteFile(Local, [new CompatRecord { Id = "moda", Verdict = CompatVerdict.Broken }]);
        var db = CompatDb.Load(Bundled, Local);

        var badge = db.For("ModA", null);
        Assert.Equal(CompatVerdict.Broken, badge.Verdict);
        Assert.Equal(CompatSource.Local, badge.Source);
        Assert.Null(badge.Record!.Notes);                       // whole-record replacement, no field merge
        Assert.Equal(ServerRole.Run, db.DefaultRoleFor("ModA")); // local record has no DefaultRole -> not the bundled one
        Assert.Single(db.LocalRecords);
        Assert.Empty(db.BundledRecords);
    }

    [Fact]
    public void Badge_FlagsUntestedVersion()
    {
        CompatDb.WriteFile(Bundled, [Rec("ModA", CompatVerdict.NeedsRecipe, "v4.2.0.7"), Rec("ModB", CompatVerdict.Works)]);
        var db = CompatDb.Load(Bundled, null);

        Assert.False(db.For("ModA", "v4.2.0.7").VersionUntested);
        Assert.False(db.For("ModA", "v4.2.0.7.0").VersionUntested); // four-part vs three-part compare
        Assert.True(db.For("ModA", "v4.3.0.0").VersionUntested);
        Assert.True(db.For("ModA", null).VersionUntested);
        Assert.False(db.For("ModB", "v9.9.9").VersionUntested);    // no tested versions recorded = no claim
    }

    [Fact]
    public void KeepSubModules_UnionAcrossRecords_ReplacesFallbackWhenMcmIsRecorded()
    {
        CompatDb.WriteFile(Bundled, [
            new CompatRecord { Id = "Bannerlord.MBOptionScreen", KeepSubModules = ["MCM.MCMSubModule"] },
            new CompatRecord { Id = "Other", KeepSubModules = ["Other.Core"] }]);
        var db = CompatDb.Load(Bundled, null);
        Assert.Equal(new[] { "MCM.MCMSubModule", "Other.Core" }, db.KeepForDependencyOnly().OrderBy(s => s));
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var rec = new CompatRecord
        {
            Id = "ImprovedGarrisons", Verdict = CompatVerdict.NeedsRecipe, TestedVersions = ["v4.2.0.7"], TestedCoopVersion = "v0.1.4",
            DefaultRole = ServerRole.Run, ServerAuthoritative = true, ClientSideBehaviors = ["IG.UiBehavior"], KeepSubModules = ["x"],
            Notes = "n", Url = "https://example", UpdatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var parsed = CompatDb.Parse(CompatDb.Serialize([rec])).Records.Single();
        Assert.Equal(rec.Id, parsed.Id);
        Assert.Equal(rec.Verdict, parsed.Verdict);
        Assert.Equal(rec.TestedVersions, parsed.TestedVersions);
        Assert.Equal(rec.TestedCoopVersion, parsed.TestedCoopVersion);
        Assert.Equal(rec.DefaultRole, parsed.DefaultRole);
        Assert.Equal(rec.ServerAuthoritative, parsed.ServerAuthoritative);
        Assert.Equal(rec.ClientSideBehaviors, parsed.ClientSideBehaviors);
        Assert.Equal(rec.KeepSubModules, parsed.KeepSubModules);
        Assert.Equal(rec.Notes, parsed.Notes);
        Assert.Equal(rec.Url, parsed.Url);
        Assert.Equal(rec.UpdatedAt, parsed.UpdatedAt);
    }

    [Fact]
    public void Parse_RejectsNewerSchema_AndLoadReportsProblemInsteadOfThrowing()
    {
        File.WriteAllText(Bundled, "{ \"SchemaVersion\": 99, \"Records\": [] }");
        File.WriteAllText(Local, "not json");
        var db = CompatDb.Load(Bundled, Local);
        Assert.Equal(2, db.Problems.Count);
        Assert.Empty(db.Records);
        Assert.Equal(ServerRole.DependencyOnly, db.DefaultRoleFor("Bannerlord.Harmony")); // fallbacks still apply
    }

    [Fact]
    public void SaveLocal_ReplacesById_AndStampsUpdatedAt()
    {
        CompatDb.SaveLocal(Local, Rec("ModA", CompatVerdict.Works));
        CompatDb.SaveLocal(Local, Rec("ModB", CompatVerdict.Broken));
        CompatDb.SaveLocal(Local, Rec("moda", CompatVerdict.Broken));
        var db = CompatDb.Load(null, Local);
        Assert.Equal(2, db.Records.Count());
        Assert.Equal(CompatVerdict.Broken, db.For("ModA", null).Verdict);
        Assert.NotNull(db.Find("ModA")!.UpdatedAt);
        Assert.False(File.Exists(Local + ".tmp"));

        CompatDb.RemoveLocal(Local, "ModA");
        CompatDb.RemoveLocal(Local, "ModB");
        Assert.False(File.Exists(Local)); // last record removed = file gone, so the bundled one shows again
    }

    [Fact]
    public void Import_NewerWins_OlderKept_MissingAdded()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = old.AddDays(1);
        CompatDb.WriteFile(Local, [
            new CompatRecord { Id = "Keep", Verdict = CompatVerdict.Works, UpdatedAt = newer },
            new CompatRecord { Id = "Update", Verdict = CompatVerdict.Works, UpdatedAt = old },
            new CompatRecord { Id = "NoDate", Verdict = CompatVerdict.Works }]);
        var import = CompatDb.Serialize([
            new CompatRecord { Id = "Keep", Verdict = CompatVerdict.Broken, UpdatedAt = old },
            new CompatRecord { Id = "Update", Verdict = CompatVerdict.Broken, UpdatedAt = newer },
            new CompatRecord { Id = "NoDate", Verdict = CompatVerdict.Broken, UpdatedAt = old },
            new CompatRecord { Id = "Added", Verdict = CompatVerdict.NeedsRecipe }]);

        var r = CompatDb.ImportLocal(Local, import);
        Assert.Equal(["Added"], r.Added);
        Assert.Equal(["NoDate", "Update"], r.Updated.OrderBy(s => s));
        Assert.Equal(["Keep"], r.Kept);

        var db = CompatDb.Load(null, Local);
        Assert.Equal(CompatVerdict.Works, db.For("Keep", null).Verdict);
        Assert.Equal(CompatVerdict.Broken, db.For("Update", null).Verdict);
        Assert.Equal(CompatVerdict.Broken, db.For("NoDate", null).Verdict);
        Assert.Equal(CompatVerdict.NeedsRecipe, db.For("Added", null).Verdict);

        // Importing the same file again changes nothing and reports everything as kept.
        var again = CompatDb.ImportLocal(Local, import);
        Assert.Empty(again.Added); Assert.Empty(again.Updated); Assert.Equal(4, again.Kept.Count);
    }

    [Fact]
    public void BundledSeedFile_LoadsAndSeedsImprovedGarrisons()
    {
        // The Content item copies the real seed next to the test assembly; this guards the shipped file against typos.
        var db = CompatDb.Load(CompatDb.BundledPath, null);
        Assert.True(File.Exists(CompatDb.BundledPath), CompatDb.BundledPath);
        Assert.Empty(db.Problems);
        var ig = db.Find("ImprovedGarrisons")!;
        Assert.Equal(CompatVerdict.NeedsRecipe, ig.Verdict);
        Assert.True(ig.ServerAuthoritative);
        Assert.Contains("ImprovedGarrisons.SaveSystem.UiBehavior", ig.ClientSideBehaviors);
        Assert.Equal(ServerRole.DependencyOnly, db.DefaultRoleFor("Bannerlord.MBOptionScreen"));
        Assert.Equal(CompatDb.FallbackKeepSubModules.OrderBy(s => s), db.KeepForDependencyOnly().OrderBy(s => s));
        Assert.Equal(ServerRole.DependencyOnly, Profile.DefaultRoleFor("Bannerlord.Harmony"));
    }
}
