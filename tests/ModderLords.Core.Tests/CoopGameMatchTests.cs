using ModderLords.Coop.Launch;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Coop builds for one game version at a time: the Workshop's Coop 0.1.7 for v1.4.8, the v1.5.7 nightlies as
/// CoopNightly. A PC's game is on one of them, so the server package and the player's Coop follow the game installed
/// here, whichever the profile last named (2026-11-19, with both builds and both servers installed).
/// </summary>
public sealed class CoopGameMatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModderLords-coop-match-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public CoopGameMatchTests()
    {
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(GamePaths.ClientBin(_game));
        File.WriteAllText(Path.Combine(GamePaths.ClientBin(_game), "Bannerlord.exe"), "fixture");
        Module("Native", "Native", null);
        Module("CoopWorkshop", "Coop", "v1.4.8");
        Module("Coop", "CoopNightly", "v1.5.7");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Game(string version) =>
        File.WriteAllText(Path.Combine(GamePaths.ClientBin(_game), "Version.xml"), $"<Version>\n\t<Singleplayer Value=\"{version}\"/>\n</Version>");

    private void Module(string folderName, string id, string? builtFor)
    {
        var folder = Path.Combine(_game, "Modules", folderName);
        Directory.CreateDirectory(Path.Combine(folder, "bin", "Win64_Shipping_Client"));
        var depends = builtFor is null ? "" : $"<DependedModules><DependedModule Id=\"Native\" DependentVersion=\"{builtFor}\"/></DependedModules>";
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"),
            $"<Module><Id value='{id}'/><Name value='{id}'/><Version value='v0.1.0'/>{depends}<SubModules/></Module>");
    }

    private string Server(string name, string runs)
    {
        var native = Path.Combine(_root, name, "engine", "Modules", "Native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "SubModule.xml"), $"<?xml version='1.0' encoding='utf-8'?>\n<Module>\n\t<Id value = \"Native\"/>\n\t<Version value = \"{runs}\"/>\n</Module>");
        return Path.Combine(_root, name);
    }

    private ClientLaunchSession.Prepared Prepare(params string[] coopIds) => ClientLaunchSession.Prepare(new Profile
    {
        Name = "coop-match", GameRoot = _game, ClientOfficialModules = ["Native"],
        Mods = coopIds.Select(id => new ProfileMod { Id = id }).ToList(),
    }, (ModuleCatalog.Scan("", _game, [], []), _game));

    // ---- the player's Coop --------------------------------------------------------------------------------------

    [Fact]
    public void A_profile_naming_the_workshop_coop_loads_the_nightly_on_a_v157_game()
    {
        Game("v1.5.7");

        var prepared = Prepare("Coop");

        Assert.Contains("CoopNightly", prepared.Plan.ModuleIds);
        Assert.DoesNotContain("Coop", prepared.Plan.ModuleIds);
        Assert.Empty(prepared.Plan.MissingModules);
        Assert.Contains(prepared.Messages, m => m.Contains("built for game v1.4.8") && m.Contains("loading CoopNightly"));
    }

    [Fact]
    public void A_profile_naming_the_nightly_loads_the_workshop_coop_on_a_v148_game()
    {
        Game("v1.4.8");

        var prepared = Prepare("CoopNightly");

        Assert.Contains("Coop", prepared.Plan.ModuleIds);
        Assert.DoesNotContain("CoopNightly", prepared.Plan.ModuleIds);
        Assert.Empty(prepared.Plan.MissingModules);
    }

    [Fact]
    public void A_coop_built_for_this_game_is_kept_and_nothing_is_said()
    {
        Game("v1.5.7");

        var prepared = Prepare("CoopNightly");

        Assert.Contains("CoopNightly", prepared.Plan.ModuleIds);
        Assert.DoesNotContain(prepared.Messages, m => m.Contains("in its place"));
    }

    [Fact]
    public void Both_coops_ticked_load_only_the_one_for_this_game()
    {
        Game("v1.5.7");

        var prepared = Prepare("CoopNightly", "Coop");

        Assert.Contains("CoopNightly", prepared.Plan.ModuleIds);
        Assert.DoesNotContain("Coop", prepared.Plan.ModuleIds);
        Assert.Contains(prepared.Messages, m => m.Contains("cannot load together"));
    }

    [Fact]
    public void Without_a_coop_for_this_game_the_profiles_coop_stays()
    {
        Game("v1.6.0");

        var prepared = Prepare("Coop");

        Assert.Contains("Coop", prepared.Plan.ModuleIds);
    }

    // ---- the server package -------------------------------------------------------------------------------------

    [Fact]
    public void A_server_package_says_which_game_it_runs()
    {
        Assert.Equal("v1.5.7", CoopGameSupport.ServerGameVersion(Server("nightly", "v1.5.7")));
        Assert.Null(CoopGameSupport.ServerGameVersion(Path.Combine(_root, "nothing")));
    }

    [Fact]
    public void The_profiles_server_is_kept_when_it_runs_this_game()
    {
        var nightly = Server("nightly", "v1.5.7");
        var workshop = Server("workshop", "v1.4.8");

        var choice = CoopGameSupport.PickServer(nightly, [workshop, nightly], "v1.5.7", CoopGameSupport.ServerGameVersion);

        Assert.Equal(nightly, choice.Root);
        Assert.Null(choice.Note);
    }

    [Fact]
    public void A_profile_server_for_another_game_gives_way_to_one_for_this_game_and_says_so()
    {
        var nightly = Server("nightly", "v1.5.7");
        var workshop = Server("workshop", "v1.4.8");

        var choice = CoopGameSupport.PickServer(workshop, [workshop, nightly], "v1.5.7", CoopGameSupport.ServerGameVersion);

        Assert.Equal(nightly, choice.Root);
        Assert.Contains("runs game v1.4.8", choice.Note);
        Assert.Contains("this PC's game is v1.5.7", choice.Note);
    }

    [Fact]
    public void Without_a_profile_server_the_one_for_this_game_is_found_whatever_the_search_order()
    {
        var workshop = Server("workshop", "v1.4.8");
        var nightly = Server("nightly", "v1.5.7");

        Assert.Equal(workshop, CoopGameSupport.PickServer(null, [workshop, nightly], "v1.4.8", CoopGameSupport.ServerGameVersion).Root);
        Assert.Equal(nightly, CoopGameSupport.PickServer(null, [workshop, nightly], "v1.5.7", CoopGameSupport.ServerGameVersion).Root);
    }

    [Fact]
    public void No_server_for_this_game_keeps_the_old_choice_with_a_warning()
    {
        var workshop = Server("workshop", "v1.4.8");

        var choice = CoopGameSupport.PickServer(null, [workshop], "v1.5.7", CoopGameSupport.ServerGameVersion);

        Assert.Equal(workshop, choice.Root);
        Assert.StartsWith("WARNING: no Coop server for this PC's game (v1.5.7)", choice.Note);
    }

    [Fact]
    public void An_unknown_game_version_changes_nothing()
    {
        var workshop = Server("workshop", "v1.4.8");
        var nightly = Server("nightly", "v1.5.7");

        var choice = CoopGameSupport.PickServer(workshop, [nightly], null, CoopGameSupport.ServerGameVersion);

        Assert.Equal(workshop, choice.Root);
        Assert.Null(choice.Note);
    }

    [Theory]
    [InlineData("v1.5.7", "v1.5.7")]
    [InlineData("v1.5.7.0", "v1.5.7")]
    [InlineData("e1.5.7.125802", "v1.5.7")]
    [InlineData(" v1.04.8 ", "v1.4.8")]
    [InlineData("v1.5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Versions_compare_by_their_first_three_numbers(string? version, string? expected) =>
        Assert.Equal(expected, CoopGameSupport.ShortVersion(version));
}
