using ModderLords.Core.Compat;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The Mods tab's settings for a mod as a compat record carries them, and the proof that a set-up was seen working.
/// Submit… sends the first once the second covers it.
/// </summary>
public sealed class ModSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ModderLords-mod-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static ProfileMod Mod(Action<ProfileMod>? change = null)
    {
        var mod = new ProfileMod { Id = "SomeMod" };
        change?.Invoke(mod);
        return mod;
    }

    [Fact]
    public void TheProfilesListsWinAndTheRecordsFillInWhereItHasNoOpinion()
    {
        var record = new CompatRecord { Id = "SomeMod", ServerExcludedFolders = ["RuntimeDataCache"], ClientExcludedFolders = ["DsData"] };

        var follows = ModSettings.From(Mod(), record);
        Assert.Equal(["RuntimeDataCache"], follows.ServerExcludedFolders);
        Assert.Equal(["DsData"], follows.ClientExcludedFolders);

        var own = ModSettings.From(Mod(m => { m.ServerExcludedFolders = []; m.ClientExcludedFolders = ["Scripts"]; }), record);
        Assert.Empty(own.ServerExcludedFolders);
        Assert.Equal(["Scripts"], own.ClientExcludedFolders);
    }

    [Fact]
    public void NamesALaunchWouldIgnoreAreNotSettings()
    {
        var settings = ModSettings.From(Mod(m => m.ServerExcludedFolders = ["bin", " RuntimeDataCache ", "runtimedatacache", "..\\x"]), null);
        Assert.Equal(["RuntimeDataCache"], settings.ServerExcludedFolders);
    }

    [Fact]
    public void SameIgnoresOrderAndCasingOfFoldersButNotARealDifference()
    {
        var a = ModSettings.From(Mod(m => m.ServerExcludedFolders = ["A", "b"]), null);
        Assert.True(a.Same(ModSettings.From(Mod(m => m.ServerExcludedFolders = ["B", "a"]), null)));
        Assert.False(a.Same(ModSettings.From(Mod(m => m.ServerExcludedFolders = ["A"]), null)));
        Assert.False(a.Same(ModSettings.From(Mod(m => { m.ServerExcludedFolders = ["A", "b"]; m.Role = ServerRole.AsShipped; }), null)));
        Assert.False(a.Same(ModSettings.From(Mod(m => { m.ServerExcludedFolders = ["A", "b"]; m.ServerAuthoritative = true; }), null)));
        Assert.False(a.Same(null));
    }

    [Fact]
    public void OverWritesTheSettingsIntoTheRecordAndKeepsTheRest()
    {
        var record = new CompatRecord
        {
            Id = "SomeMod", Verdict = CompatVerdict.NeedsRecipe, Notes = "n", KeepSubModules = ["K"],
            ServerAuthoritative = true, ClientSideBehaviors = ["Old.Ui"], ServerExcludedFolders = ["Gone"],
        };
        var mod = Mod(m => { m.Role = ServerRole.DependencyOnly; m.ServerExcludedFolders = ["RuntimeDataCache"]; m.ClientExcludedFolders = ["DsData"]; });

        var sent = ModSettings.From(mod, record).Over("SomeMod", record);

        Assert.Equal(ServerRole.DependencyOnly, sent.DefaultRole);
        Assert.False(sent.ServerAuthoritative);
        Assert.Equal(["RuntimeDataCache"], sent.ServerExcludedFolders);
        Assert.Equal(["DsData"], sent.ClientExcludedFolders);
        Assert.Equal((CompatVerdict.NeedsRecipe, "n"), (sent.Verdict, sent.Notes));
        Assert.Equal(["K"], sent.KeepSubModules);
        // The record handed in is the database's own object and must come out untouched.
        Assert.Equal(["Gone"], record.ServerExcludedFolders);
    }

    [Fact]
    public void OverSaysNothingAboutARoleNobodyChanged()
    {
        Assert.Null(ModSettings.From(Mod(), null).Over("SomeMod", null).DefaultRole);
        Assert.Null(ModSettings.From(Mod(), null).Over("SomeMod", null).ServerAuthoritative);
        // Harmony is DependencyOnly without any record; that is its unchanged state.
        var harmony = new ProfileMod { Id = "Bannerlord.Harmony", Role = ServerRole.DependencyOnly };
        Assert.Null(ModSettings.From(harmony, null).Over(harmony.Id, null).DefaultRole);
        harmony.Role = ServerRole.Run;
        Assert.Equal(ServerRole.Run, ModSettings.From(harmony, null).Over(harmony.Id, null).DefaultRole);
    }

    [Fact]
    public void ARecordBuiltFromSettingsPassesTheIntakeGate()
    {
        var mod = Mod(m => { m.ServerAuthoritative = true; m.ClientSideBehaviors = ["Some.Ui"]; m.ServerExcludedFolders = ["RuntimeDataCache"]; m.ClientExcludedFolders = ["DsData"]; });
        var json = CompatSubmission.RecordJson(ModSettings.From(mod, null).Over("SomeMod", null));

        var checkedRecord = CompatIntake.ValidateSubmission(json, "SomeMod");

        Assert.Empty(checkedRecord.Problems);
        Assert.Equal(["DsData"], checkedRecord.Record!.ClientExcludedFolders);
    }

    [Fact]
    public void TheRecordsServerOnlyFoldersApplyToAGameUntilTheProfileSaysOtherwise()
    {
        var folder = Path.Combine(_dir, "SomeMod");
        Directory.CreateDirectory(Path.Combine(folder, "DsData"));
        var module = new DiscoveredModule("SomeMod", "v1.0.0", folder, ModuleSourceKind.Custom,
            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = "SomeMod", Name = "SomeMod" });
        var bundled = Path.Combine(_dir, CompatDb.BundledFileName);
        CompatDb.WriteFile(bundled, [new CompatRecord { Id = "SomeMod", ClientExcludedFolders = ["DsData"] }]);
        var db = CompatDb.Load(bundled, null);

        var follows = ClientFolderExclusions.Resolve([module], new Profile { Mods = [Mod()] }, [], db);
        Assert.Equal(["DsData"], follows["SomeMod"]);

        var overruled = ClientFolderExclusions.Resolve([module], new Profile { Mods = [Mod(m => m.ClientExcludedFolders = [])] }, [], db);
        Assert.Empty(overruled);
    }

    [Fact]
    public void ALocalRecordsServerOnlyFoldersReplaceTheBundledOnesAndOnlyTheDifferenceIsStored()
    {
        var bundled = new CompatRecord { Id = "SomeMod", ClientExcludedFolders = ["DsData"] };
        var local = new CompatRecord { Id = "SomeMod", ClientExcludedFolders = ["Scripts"] };

        Assert.Equal(["Scripts"], CompatDb.Merge(bundled, local).ClientExcludedFolders);
        Assert.Equal(["DsData"], CompatDb.Merge(bundled, new CompatRecord { Id = "SomeMod" }).ClientExcludedFolders);
        Assert.Null(CompatDb.LocalPart(bundled.Clone(), bundled));
        Assert.Equal(["Scripts"], CompatDb.LocalPart(CompatDb.Merge(bundled, local), bundled)!.ClientExcludedFolders);
    }

    // ---- proof -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWatchFiresOnceForTheFirstPlayerOnTheMap()
    {
        var watch = new JoinWatch();
        Assert.False(watch.Observe("[DedicatedServer] SERVING"));
        Assert.False(watch.Observe("@DS@{\"ev\":\"players\",\"list\":[]}"));
        Assert.False(watch.Observe("@DS@{\"ev\":\"players\",\"list\":[{\"id\":1,\"name\":\"A\",\"state\":\"loading\",\"addr\":\"x\"}]}"));
        Assert.True(watch.Observe("@DS@{\"ev\":\"players\",\"list\":[{\"id\":1,\"name\":\"A\",\"state\":\"on map\",\"addr\":\"x\"}]}"));
        Assert.False(watch.Observe("@DS@{\"ev\":\"players\",\"list\":[{\"id\":1,\"name\":\"A\",\"state\":\"on map\",\"addr\":\"x\"}]}"));
        Assert.True(watch.Joined);
    }

    [Fact]
    public void AProofCoversOnlyTheVersionsAndSettingsItWasMadeWith()
    {
        var settings = ModSettings.From(Mod(m => m.ServerExcludedFolders = ["RuntimeDataCache"]), null);
        var proof = new CompatProof("SomeMod", "v1.2.0", "v0.1.5", DateTime.UtcNow, settings);

        Assert.True(proof.Covers("v1.2.0", "v0.1.5", ModSettings.From(Mod(m => m.ServerExcludedFolders = ["runtimedatacache"]), null)));
        Assert.False(proof.Covers("v1.3.0", "v0.1.5", settings));
        Assert.False(proof.Covers("v1.2.0", "v0.1.6", settings));
        Assert.False(proof.Covers("v1.2.0", "v0.1.5", ModSettings.From(Mod(), null)));
    }

    [Fact]
    public void TheStoreKeepsTheLatestProofPerModAndShrugsAtABrokenFile()
    {
        var path = Path.Combine(_dir, CompatProofStore.FileName);
        var settings = ModSettings.From(Mod(m => { m.Role = ServerRole.AsShipped; m.ClientExcludedFolders = ["DsData"]; }), null);
        Assert.Null(CompatProofStore.Find(path, "SomeMod"));

        CompatProofStore.Add(path, [new CompatProof("SomeMod", "v1", null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), settings), new CompatProof("Other", "v1", null, DateTime.UtcNow, settings)]);
        CompatProofStore.Add(path, [new CompatProof("somemod", "v2", "v0.1.5", new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), settings)]);

        Assert.Equal(2, CompatProofStore.Load(path).Count);
        var found = CompatProofStore.Find(path, "SomeMod")!;
        Assert.Equal(("v2", "v0.1.5"), (found.ModVersion, found.CoopVersion));
        Assert.True(found.Settings.Same(settings));
        // Nothing about the session is kept: the file has the mod, the versions, the date and the settings.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("addr", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name", text, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(CompatProofStore.Load(path));
    }

    [Fact]
    public void TheFormsNoteNamesTheDateAndNothingElseAboutTheSession()
    {
        var link = CompatSubmission.Build(new CompatRecord { Id = "SomeMod", Verdict = CompatVerdict.Works }, "v1", "v0.1.5",
            CompatSubmission.ProofNote(new DateTime(2026, 10, 6, 23, 59, 0, DateTimeKind.Utc)));

        Assert.Contains("&notes=", link.Url);
        Assert.Contains("2026-10-06", Uri.UnescapeDataString(link.Url));
        Assert.DoesNotContain("&notes=", CompatSubmission.Build(new CompatRecord { Id = "SomeMod" }, "v1", null).Url);
    }
}
