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
