using ModderLords.Core.Export;
using ModderLords.Core.Overlay;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// A shared mod list carries the order a PLAYER loads it in. It used to carry whichever engine order the exporting
/// mode happened to compute, so a host handed out "Coop last" — true of the dedicated server, and the arrangement
/// that crashes any client running mods that patch Coop.
/// </summary>
public class ModListFileClientOrderTests
{
    [Fact]
    public void Import_places_Coop_at_its_recorded_position()
    {
        var file = new ModListFile
        {
            FormatVersion = 2,
            Coop = new ModListFile.CoopRef("CoopNightly", "v0.1.5"),
            Mods =
            [
                new ModListFile.ModEntry("Bannerlord.Harmony", "v2.4.2", null, ServerRole.DependencyOnly, false),
                new ModListFile.ModEntry("CoopNightly", "v0.1.5", null, ServerRole.AsShipped, false),
                new ModListFile.ModEntry("CoopMarriage", "v1.1.1", null, ServerRole.Run, false),
            ],
        };

        var ids = file.ToProfile("p").Mods.Select(m => m.Id).ToList();

        Assert.Equal(["Bannerlord.Harmony", "CoopNightly", "CoopMarriage"], ids);
    }

    [Fact]
    public void A_version_1_file_still_imports_with_Coop_last_and_says_so()
    {
        var file = new ModListFile
        {
            FormatVersion = 1,
            Coop = new ModListFile.CoopRef("CoopNightly", "v0.1.5"),
            Mods = [new ModListFile.ModEntry("CoopMarriage", "v1.1.1", null, ServerRole.Run, false)],
        };

        Assert.True(file.CoopPositionUnknown);
        Assert.Equal(["CoopMarriage", "CoopNightly"], file.ToProfile("p").Mods.Select(m => m.Id));
    }

    [Fact]
    public void A_version_2_file_that_lists_Coop_does_not_claim_its_position_is_unknown()
    {
        var file = new ModListFile
        {
            FormatVersion = 2,
            Coop = new ModListFile.CoopRef("CoopNightly", "v0.1.5"),
            Mods = [new ModListFile.ModEntry("CoopNightly", "v0.1.5", null, ServerRole.AsShipped, false)],
        };

        Assert.False(file.CoopPositionUnknown);
        Assert.Single(file.ToProfile("p").Mods);   // never added twice
    }

    /// <summary>The sync enables Coop and never version-checks it, so an entry would only raise a mismatch.</summary>
    [Fact]
    public void ToClientEntries_leaves_Coop_out_and_ToOrder_keeps_it_in()
    {
        var file = new ModListFile
        {
            FormatVersion = 2,
            Mods =
            [
                new ModListFile.ModEntry("CoopNightly", "v0.1.5", null, ServerRole.AsShipped, false),
                new ModListFile.ModEntry("CoopMarriage", "v1.1.1", null, ServerRole.Run, false),
            ],
        };

        Assert.Equal(["CoopMarriage"], file.ToClientEntries().Select(e => e.Id));
        Assert.Equal(["CoopNightly", "CoopMarriage"], file.ToOrder().ModuleIds);
    }

    [Fact]
    public void A_newer_format_is_refused_and_this_one_is_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mbc-modlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var current = Path.Combine(dir, "current.json");
            ModListFile.Write(current, new ModListFile
            {
                Mods = [new ModListFile.ModEntry("A", "v1", null, ServerRole.Run, false)],
            });
            Assert.Equal(ModListFile.CurrentFormatVersion, ModListFile.Read(current).FormatVersion);
            Assert.Equal(2, ModListFile.CurrentFormatVersion);

            var future = Path.Combine(dir, "future.json");
            File.WriteAllText(future, """{"FormatVersion":99,"Mods":[{"Id":"A","Version":"v1","Role":"Run"}]}""");
            Assert.Throws<InvalidOperationException>(() => ModListFile.Read(future));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>
/// The check that spots the one arrangement that crashes Bannerlord at startup, so the launcher can offer to fix it
/// instead of rewriting somebody's profile behind their back.
/// </summary>
public class CoopOrderCheckTests
{
    private static ProfileMod M(string id, bool enabled = true) => new() { Id = id, Enabled = enabled };

    [Fact]
    public void Reports_a_coop_patching_mod_placed_before_Coop()
    {
        var finding = CoopOrderCheck.Inspect([M("CoopMarriage"), M("CoopNightly")], ["CoopMarriage"]);

        Assert.NotNull(finding);
        Assert.Equal("CoopNightly", finding!.CoopId);
        Assert.Equal(["CoopMarriage"], finding.ModsBeforeCoop);
    }

    /// <summary>
    /// Only "My order wins" actually ships the crashing order; by default the sorter moves the mod itself, so
    /// claiming the game will crash would be false.
    /// </summary>
    [Fact]
    public void Only_My_order_wins_is_told_the_game_will_crash()
    {
        var finding = CoopOrderCheck.Inspect([M("CoopMarriage"), M("CoopNightly")], ["CoopMarriage"])!;

        Assert.Contains("crash", finding.Message(manualLoadOrder: true), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("crash", finding.Message(manualLoadOrder: false), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anyway", finding.Message(manualLoadOrder: false), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Says_nothing_when_Coop_already_loads_first()
        => Assert.Null(CoopOrderCheck.Inspect([M("CoopNightly"), M("CoopMarriage")], ["CoopMarriage"]));

    /// <summary>Coop last is a perfectly good order for someone running no mods that patch it.</summary>
    [Fact]
    public void Says_nothing_about_Coop_last_when_nothing_patches_it()
        => Assert.Null(CoopOrderCheck.Inspect([M("ImprovedGarrisons"), M("CoopNightly")], ["CoopMarriage"]));

    [Fact]
    public void Ignores_a_disabled_mod()
        => Assert.Null(CoopOrderCheck.Inspect([M("CoopMarriage", enabled: false), M("CoopNightly")], ["CoopMarriage"]));

    [Fact]
    public void Says_nothing_when_Coop_is_not_in_the_profile_at_all()
        => Assert.Null(CoopOrderCheck.Inspect([M("CoopMarriage")], ["CoopMarriage"]));

    // ---- mods nobody has curated ---------------------------------------------------------------------------------

    private static readonly Func<string, bool> NothingDeclaresOrder = _ => false;

    /// <summary>
    /// The whole scheme fails open without this: a coop-binding mod nobody has written a record for loads first and
    /// takes the game down, with the launcher none the wiser.
    /// </summary>
    [Fact]
    public void An_uncurated_mod_whose_submodule_binds_to_Coop_is_reported()
    {
        var found = CoopOrderCheck.UnplacedCoopBinders(
            [M("SomeNewMod"), M("CoopNightly")], coopReferencingIds: ["SomeNewMod"],
            knownToFollowCoop: [], declaresOrder: NothingDeclaresOrder);

        Assert.Equal(["SomeNewMod"], found);
    }

    [Fact]
    public void A_mod_already_curated_or_declaring_the_order_is_not_reported_twice()
    {
        Assert.Empty(CoopOrderCheck.UnplacedCoopBinders(
            [M("CoopMarriage"), M("CoopNightly")], ["CoopMarriage"], ["CoopMarriage"], NothingDeclaresOrder));
        Assert.Empty(CoopOrderCheck.UnplacedCoopBinders(
            [M("TAOM.CoopCompat"), M("CoopNightly")], ["TAOM.CoopCompat"], [], id => id == "TAOM.CoopCompat"));
    }

    /// <summary>
    /// ModularSmithing2 ships a Coop adapter but loads it by reflection, so its SUBMODULE assembly has no Coop
    /// reference and it never appears in the referencing set. Reporting it would be noise that pushes people to
    /// write records that make the launcher shuffle a mod for nothing.
    /// </summary>
    [Fact]
    public void A_mod_that_keeps_Coop_out_of_its_submodule_is_not_reported()
        => Assert.Empty(CoopOrderCheck.UnplacedCoopBinders(
            [M("ModularSmithing2"), M("CoopNightly")], coopReferencingIds: [], knownToFollowCoop: [], NothingDeclaresOrder));

    [Fact]
    public void A_binder_the_user_already_placed_after_Coop_is_not_reported()
        => Assert.Empty(CoopOrderCheck.UnplacedCoopBinders(
            [M("CoopNightly"), M("SomeNewMod")], ["SomeNewMod"], [], NothingDeclaresOrder));

    [Fact]
    public void A_disabled_binder_is_not_reported()
        => Assert.Empty(CoopOrderCheck.UnplacedCoopBinders(
            [M("SomeNewMod", enabled: false), M("CoopNightly")], ["SomeNewMod"], [], NothingDeclaresOrder));
}

/// <summary>
/// An imported list whose mods are not all installed is kept as a profile, and later rebuilt from it to apply to the
/// launcher again. The rebuild must give back the same list, missing mods included, in the same order.
/// </summary>
public class ModListFilePendingTests
{
    private static readonly ModListFile Shared = new()
    {
        Name = "friends",
        Mods =
        [
            new("Harmony", "2.3", "https://steamcommunity.com/sharedfiles/filedetails/?id=2859188632", ServerRole.DependencyOnly, false),
            new("NotYetDownloaded", "1.0", "https://steamcommunity.com/sharedfiles/filedetails/?id=3000000001", ServerRole.Run, true),
            new("CoopNightly", "0.9", null, ServerRole.AsShipped, false),
            new("PatchesCoop", "1.1", null, ServerRole.Run, false),
        ],
        ClientOfficialModules = ["Native", "SandBoxCore", "SandBox"],
    };

    [Fact]
    public void AsListed_round_trips_an_imported_list()
    {
        var back = ModListFile.AsListed(Shared.ToProfile("friends"));
        Assert.Equal(Shared.Mods, back.Mods);
        Assert.Equal(Shared.ClientOfficialModules, back.ClientOfficialModules);
        Assert.Equal(Shared.ToOrder().ModuleIds, back.ToOrder().ModuleIds);
    }

    [Fact]
    public void AsListed_leaves_out_unticked_mods()
    {
        var profile = Shared.ToProfile("friends");
        profile.Mods.Single(m => m.Id == "PatchesCoop").Enabled = false;
        Assert.DoesNotContain(ModListFile.AsListed(profile).Mods, m => m.Id == "PatchesCoop");
    }

    [Fact]
    public void NotInstalled_names_only_what_is_absent_and_never_Coop()
    {
        var installed = new HashSet<string>(["harmony", "PatchesCoop"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["NotYetDownloaded"], Shared.NotInstalled(installed));
    }

    [Fact]
    public void Pending_flag_survives_a_save()
    {
        var profile = Shared.ToProfile("friends");
        profile.PendingLauncherApply = true;
        var json = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!.PendingLauncherApply);
    }
}

/// <summary>
/// A shared list carries, per mod, which folders are for the server only and which for the game only: whoever
/// receives it may host or may play. Both lists were added without a new format number, so the two directions that
/// could break are checked here: a list written before them, and an older launcher reading one written after.
/// </summary>
public class ModListFileFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mbc-modlist-folders-" + Guid.NewGuid().ToString("N"));

    public ModListFileFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly ModListFile WithFolders = new()
    {
        Name = "friends",
        Mods =
        [
            new("NoOpinion", "1.0", null, ServerRole.Run, false),
            new("LeaveNothingOut", "1.0", null, ServerRole.Run, false) { ServerExcludedFolders = [] },
            new("Both", "1.0", null, ServerRole.Run, true) { ServerExcludedFolders = ["RuntimeDataCache"], ClientExcludedFolders = ["DsData", "ServerScripts"] },
        ],
    };

    private ModListFile ThroughAFile(ModListFile file)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        ModListFile.Write(path, file);
        return ModListFile.Read(path);
    }

    /// <summary>
    /// For the server's list, absent and empty are different answers: absent follows the importer's compat database,
    /// empty overrules it. A round trip that blurred them would silently change what a host's server is shown.
    /// </summary>
    [Fact]
    public void Both_lists_survive_a_file_with_null_and_empty_kept_apart()
    {
        var read = ThroughAFile(WithFolders);

        Assert.Null(read.Mods[0].ServerExcludedFolders);
        Assert.Null(read.Mods[0].ClientExcludedFolders);
        Assert.NotNull(read.Mods[1].ServerExcludedFolders);
        Assert.Empty(read.Mods[1].ServerExcludedFolders!);
        Assert.Equal(["RuntimeDataCache"], read.Mods[2].ServerExcludedFolders);
        Assert.Equal(["DsData", "ServerScripts"], read.Mods[2].ClientExcludedFolders);
        Assert.Equal(WithFolders.Mods, read.Mods);
        Assert.Equal(ModListFile.CurrentFormatVersion, read.FormatVersion);
    }

    [Fact]
    public void Import_and_export_carry_both_lists_through_a_profile()
    {
        var profile = ThroughAFile(WithFolders).ToProfile("friends");

        Assert.Null(profile.Mods[0].ServerExcludedFolders);
        Assert.Null(profile.Mods[0].ClientExcludedFolders);
        Assert.Empty(profile.Mods[1].ServerExcludedFolders!);
        Assert.Equal(["RuntimeDataCache"], profile.Mods[2].ServerExcludedFolders);
        Assert.Equal(["DsData", "ServerScripts"], profile.Mods[2].ClientExcludedFolders);

        var back = ModListFile.AsListed(profile);
        Assert.Equal(WithFolders.Mods, back.Mods);
        // The exported lists are copies: editing the profile afterwards must not rewrite a file already built.
        profile.Mods[2].ClientExcludedFolders!.Clear();
        Assert.Equal(["DsData", "ServerScripts"], back.Mods[2].ClientExcludedFolders);

        // "Server only" has no third state, so an empty list is not worth a line in the file.
        profile.Mods[2].ClientExcludedFolders = [];
        Assert.Null(ModListFile.AsListed(profile).Mods[2].ClientExcludedFolders);
    }

    [Fact]
    public void A_list_from_before_the_folder_lists_reads_as_nothing_said()
    {
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, """{"FormatVersion":2,"Mods":[{"Id":"A","Version":"v1","Role":"Run","ServerAuthoritative":true}]}""");

        var entry = Assert.Single(ModListFile.Read(path).Mods);

        Assert.Null(entry.ServerExcludedFolders);
        Assert.Null(entry.ClientExcludedFolders);
        Assert.True(entry.ServerAuthoritative);
        var mod = Assert.Single(ModListFile.Read(path).ToProfile("p").Mods);
        Assert.Null(mod.ServerExcludedFolders);
        Assert.Null(mod.ClientExcludedFolders);
    }

    /// <summary>A mod nobody set folders for is written exactly as before, so most files do not change at all.</summary>
    [Fact]
    public void An_entry_without_folder_lists_is_written_as_it_always_was()
    {
        var json = new ModListFile { Mods = [new("A", "v1", null, ServerRole.Run, false)] }.ToJson();
        Assert.DoesNotContain("ExcludedFolders", json);
        Assert.Contains("\"ServerExcludedFolders\": []", new ModListFile { Mods = [WithFolders.Mods[1]] }.ToJson());
    }

    // The entry as launchers before the folder lists declared it, read with the options they read it with.
    private sealed record OldEntry(string Id, string Version, string? Source, ServerRole Role, bool ServerAuthoritative);
    private sealed record OldFile
    {
        public int FormatVersion { get; init; }
        public IReadOnlyList<OldEntry> Mods { get; init; } = [];
    }

    /// <summary>
    /// The format number was not bumped, so an older launcher will open a new file. It has to read it: the unknown
    /// properties are skipped and everything it knew about is still where it was.
    /// </summary>
    [Fact]
    public void An_older_launcher_reads_a_file_that_has_the_folder_lists()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        var old = System.Text.Json.JsonSerializer.Deserialize<OldFile>(WithFolders.ToJson(), options)!;

        Assert.Equal(2, old.FormatVersion);
        Assert.Equal(["NoOpinion", "LeaveNothingOut", "Both"], old.Mods.Select(m => m.Id));
        Assert.True(old.Mods[2].ServerAuthoritative);
        Assert.Equal(ServerRole.Run, old.Mods[2].Role);
    }
}
