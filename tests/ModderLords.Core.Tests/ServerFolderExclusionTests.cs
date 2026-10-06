using System.Runtime.Versioning;
using Bannerlord.ModuleManager;
using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Folders a host asks to keep away from the dedicated server. Opt-in per mod: a host suspected RuntimeDataCache of
/// hanging the server and nothing has confirmed it, so the default has to stay "the server sees the whole mod" and
/// every way of asking has to be visible in the plan notes.
/// </summary>
public sealed class ServerFolderExclusionPlannerTests
{
    /// <summary>A mod that would get a direct junction: no client-only tags, no bins on disk (the path does not exist).</summary>
    private static DiscoveredModule Mod(string id = "Mod") =>
        new(id, "v1.0.0", @"X\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended { Id = id, Name = id });

    private static OverlayEntry Plan(IReadOnlyList<string>? profile, IReadOnlyList<string>? record, params string[] foldersOnDisk) =>
        Assert.Single(OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(Mod(), ServerRole.Run)],
            scan: _ => new ScanResult("Mod", ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false),
            record: _ => record is null ? null : new CompatRecord { Id = "Mod", ServerExcludedFolders = record.ToList() },
            typeNames: _ => [],
            profileExcludedFolders: _ => profile,
            topLevelFolders: _ => foldersOnDisk).Entries);

    [Fact]
    public void Nothing_asked_for_keeps_the_direct_junction_and_never_lists_the_folder()
    {
        var entry = Assert.Single(OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(Mod(), ServerRole.Run)],
            scan: _ => new ScanResult("Mod", ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false),
            record: _ => null, typeNames: _ => [],
            topLevelFolders: _ => throw new Exception("the mod folder must not be read when nothing is left out")).Entries);

        Assert.Equal(OverlayKind.DirectJunction, entry.Kind);
        Assert.Empty(entry.ExcludedFolders ?? []);
    }

    [Fact]
    public void An_excluded_folder_that_exists_forces_a_shadow_and_is_named_in_the_notes()
    {
        var entry = Plan(profile: ["runtimedatacache"], record: null, "ModuleData", "RuntimeDataCache");

        Assert.Equal(OverlayKind.Shadow, entry.Kind);
        Assert.Equal(Path.Combine(@"X\overlay", "Mod"), entry.ShadowPath);
        // Named as it is on disk, not as it was typed.
        Assert.Equal(["RuntimeDataCache"], entry.ExcludedFolders);
        Assert.Contains("left out on the server: RuntimeDataCache", entry.Notes);
    }

    /// <summary>
    /// A record that lists RuntimeDataCache applies to every copy of the mod, including the ones that never had the
    /// folder. Those must keep their direct junction: a shadow that leaves nothing out is all cost.
    /// </summary>
    [Fact]
    public void An_excluded_folder_that_does_not_exist_changes_nothing_but_is_reported()
    {
        var entry = Plan(profile: null, record: ["RuntimeDataCache"], "ModuleData");

        Assert.Equal(OverlayKind.DirectJunction, entry.Kind);
        Assert.Null(entry.ShadowPath);
        Assert.Empty(entry.ExcludedFolders ?? []);
        Assert.Contains(entry.Notes, n => n.StartsWith(OverlayPlanner.NotLeftOutNote) && n.Contains("RuntimeDataCache"));
        Assert.DoesNotContain(entry.Notes, n => n.StartsWith(OverlayPlanner.LeftOutNote));
    }

    [Fact]
    public void With_no_opinion_in_the_profile_the_compat_record_decides()
    {
        var entry = Plan(profile: null, record: ["RuntimeDataCache"], "RuntimeDataCache", "AssetSources");

        Assert.Equal(OverlayKind.Shadow, entry.Kind);
        Assert.Equal(["RuntimeDataCache"], entry.ExcludedFolders);
    }

    /// <summary>Empty is not "no opinion": it is the host saying the record is wrong for their server.</summary>
    [Fact]
    public void An_empty_profile_list_overrules_the_compat_record()
    {
        var entry = Plan(profile: [], record: ["RuntimeDataCache"], "RuntimeDataCache");

        Assert.Equal(OverlayKind.DirectJunction, entry.Kind);
        Assert.Empty(entry.ExcludedFolders ?? []);
        Assert.DoesNotContain(entry.Notes, n => n.Contains("left out"));
    }

    [Fact]
    public void A_profile_list_replaces_the_compat_records_list_rather_than_adding_to_it()
    {
        var entry = Plan(profile: ["AssetSources"], record: ["RuntimeDataCache"], "RuntimeDataCache", "AssetSources");

        Assert.Equal(["AssetSources"], entry.ExcludedFolders);
    }

    [Theory]
    [InlineData(@"RuntimeDataCache\sub")]
    [InlineData("RuntimeDataCache/sub")]
    [InlineData("..")]
    [InlineData(@"..\OtherMod")]
    [InlineData(@"C:\Windows")]
    [InlineData("bin")]
    [InlineData("BIN")]
    [InlineData("SubModule.xml")]
    public void A_name_that_is_not_a_plain_top_level_folder_is_refused_and_said_so(string name)
    {
        // Every one of these "exists" as far as the injected listing is concerned, so only the name check stops it.
        var entry = Plan(profile: [name], record: null, name, "bin", "..", "ModuleData");

        Assert.Equal(OverlayKind.DirectJunction, entry.Kind);
        Assert.Empty(entry.ExcludedFolders ?? []);
        Assert.Contains(entry.Notes, n => n.StartsWith("WARNING") && n.Contains($"\"{name}\""));
    }

    [Fact]
    public void A_refused_name_does_not_take_the_good_ones_down_with_it()
    {
        var entry = Plan(profile: ["bin", "RuntimeDataCache", @"a\b"], record: null, "bin", "RuntimeDataCache");

        Assert.Equal(["RuntimeDataCache"], entry.ExcludedFolders);
        Assert.Contains(entry.Notes, n => n.StartsWith("WARNING") && n.Contains("\"bin\"") && n.Contains("\"a\\b\""));
    }

    [Fact]
    public void Names_are_validated_and_split_the_same_way_for_the_dialog()
    {
        Assert.Equal(["RuntimeDataCache", "AssetSources"],
            ServerFolderExclusions.ParseLines("  RuntimeDataCache \r\n\r\nAssetSources\\\nruntimedatacache\n"));
        Assert.Empty(ServerFolderExclusions.ParseLines(null));
        Assert.Null(ServerFolderExclusions.Problem("RuntimeDataCache"));
        Assert.Null(ServerFolderExclusions.Problem("Scene Edit Data"));
        Assert.NotNull(ServerFolderExclusions.Problem(" "));
        Assert.NotNull(ServerFolderExclusions.Problem("."));
    }

    /// <summary>The three states have to survive the profile file, or "leave nothing out" silently becomes "no opinion".</summary>
    [Fact]
    public void Null_empty_and_named_all_survive_a_profile_round_trip()
    {
        var profile = new Profile
        {
            Mods =
            [
                new ProfileMod { Id = "NoOpinion" },
                new ProfileMod { Id = "Nothing", ServerExcludedFolders = [] },
                new ProfileMod { Id = "Named", ServerExcludedFolders = ["RuntimeDataCache"] },
            ],
        };

        var copy = ProfileStore.Snapshot(profile);

        Assert.Null(copy.Mods[0].ServerExcludedFolders);
        Assert.NotNull(copy.Mods[1].ServerExcludedFolders);
        Assert.Empty(copy.Mods[1].ServerExcludedFolders!);
        Assert.Equal(["RuntimeDataCache"], copy.Mods[2].ServerExcludedFolders);
    }

    [Fact]
    public void The_compat_record_keeps_its_list_through_Clone_and_the_file()
    {
        var rec = new CompatRecord { Id = "X", ServerExcludedFolders = ["RuntimeDataCache", "AssetSources"] };

        var clone = rec.Clone();
        Assert.Equal(rec.ServerExcludedFolders, clone.ServerExcludedFolders);
        Assert.NotSame(rec.ServerExcludedFolders, clone.ServerExcludedFolders);
        Assert.Equal(rec.ServerExcludedFolders, CompatDb.Parse(CompatDb.Serialize([rec])).Records.Single().ServerExcludedFolders);
        // A record written before the field existed reads as "leave nothing out".
        Assert.Empty(CompatDb.Parse("{ \"Records\": [ { \"Id\": \"Old\" } ] }").Records.Single().ServerExcludedFolders);
    }
}

/// <summary>The same thing against real folders and real junctions. Windows only (junctions).</summary>
[SupportedOSPlatform("windows")]
public sealed class ServerFolderExclusionOverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbc-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _engineModules;
    private readonly string _overlay;

    public ServerFolderExclusionOverlayTests()
    {
        _engineModules = Path.Combine(_root, "engine", "Modules");
        _overlay = Path.Combine(_root, "overlay");
        Directory.CreateDirectory(_engineModules);
    }

    public void Dispose()
    {
        // Junctions must be removed as links, never recursed into.
        foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (Junction.IsJunction(d)) Directory.Delete(d, false);
        Directory.Delete(_root, true);
    }

    /// <summary>A server-ready mod (direct junction by default) with a data folder and a client-only cache folder.</summary>
    private DiscoveredModule MakeMod(string id)
    {
        var folder = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(Path.Combine(folder, "bin", "Win64_Shipping_Server"));
        File.WriteAllText(Path.Combine(folder, "bin", "Win64_Shipping_Server", id + ".dll"), "not really");
        Directory.CreateDirectory(Path.Combine(folder, "ModuleData"));
        File.WriteAllText(Path.Combine(folder, "ModuleData", "data.xml"), "<x/>");
        Directory.CreateDirectory(Path.Combine(folder, "RuntimeDataCache"));
        File.WriteAllText(Path.Combine(folder, "RuntimeDataCache", "cache.bin"), "client only");
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Module><Id value="{id}"/><Name value="{id}"/><Version value="v1.2.3"/>
            <SubModules><SubModule><Name value="{id}"/><DLLName value="{id}.dll"/><SubModuleClassType value="{id}.Sub"/></SubModule></SubModules></Module>
            """);
        return ModuleCatalog.TryParse(folder, ModuleSourceKind.Custom, out var problem) ?? throw new Exception(problem);
    }

    private OverlayPlan Plan(DiscoveredModule mod, IReadOnlyList<string>? profile, IReadOnlyList<string>? record = null) =>
        OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.Run)],
            record: _ => record is null ? null : new CompatRecord { Id = mod.Id, ServerExcludedFolders = record.ToList() },
            profileExcludedFolders: _ => profile);

    [Fact]
    public void The_server_sees_the_mod_without_the_excluded_folder_and_the_mod_itself_is_untouched()
    {
        var mod = MakeMod("Cached");
        var plan = Plan(mod, ["RUNTIMEDATACACHE"]);
        Assert.Equal(OverlayKind.Shadow, plan.Entries.Single().Kind);

        var result = new OverlayApplier().Apply(plan);
        Assert.Empty(result.Warnings);

        var seen = Path.Combine(_engineModules, "Cached");
        Assert.False(Directory.Exists(Path.Combine(seen, "RuntimeDataCache")));
        Assert.True(File.Exists(Path.Combine(seen, "ModuleData", "data.xml")));
        Assert.True(File.Exists(Path.Combine(seen, "bin", "Win64_Shipping_Server", "Cached.dll")));
        Assert.Contains("<Id value=\"Cached\"", File.ReadAllText(Path.Combine(seen, "SubModule.xml")));
        Assert.True(File.Exists(Path.Combine(mod.FolderPath, "RuntimeDataCache", "cache.bin")));
    }

    /// <summary>
    /// The case that would make the option look broken: the mod was already shadowed by an earlier launch, so the
    /// shadow already holds a junction for the folder. Excluding it afterwards has to take that junction away, not
    /// merely stop creating it.
    /// </summary>
    [Fact]
    public void A_junction_left_by_an_earlier_launch_is_removed_once_the_folder_is_excluded()
    {
        var mod = MakeMod("Stale");
        // Force a shadow without excluding anything, as DependencyOnly does, so the junction exists first.
        var before = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.DependencyOnly)], record: _ => null);
        new OverlayApplier().Apply(before);
        var stale = Path.Combine(before.Entries.Single().ShadowPath!, "RuntimeDataCache");
        Assert.True(Junction.IsJunction(stale));

        var after = OverlayPlanner.Plan(_overlay, _engineModules, [new ModSelection(mod, ServerRole.DependencyOnly)],
            record: _ => null, profileExcludedFolders: _ => ["RuntimeDataCache"]);
        Assert.Empty(new OverlayApplier().Apply(after).Warnings);

        Assert.False(Directory.Exists(stale));
        Assert.True(Junction.IsJunction(Path.Combine(after.Entries.Single().ShadowPath!, "ModuleData")));
        // Removed as a link: what it pointed at is still there.
        Assert.True(File.Exists(Path.Combine(mod.FolderPath, "RuntimeDataCache", "cache.bin")));
    }

    /// <summary>Both directions of the switch, on the same engine link: direct, then shadow, then direct again.</summary>
    [Fact]
    public void Turning_the_exclusion_on_and_off_repoints_the_engine_link()
    {
        var mod = MakeMod("Switched");
        var link = Path.Combine(_engineModules, "Switched");

        new OverlayApplier().Apply(Plan(mod, null));
        Assert.True(Junction.PointsTo(link, mod.FolderPath));
        Assert.True(Directory.Exists(Path.Combine(link, "RuntimeDataCache")));

        new OverlayApplier().Apply(Plan(mod, ["RuntimeDataCache"]));
        Assert.True(Junction.PointsTo(link, Path.Combine(_overlay, "Switched")));
        Assert.False(Directory.Exists(Path.Combine(link, "RuntimeDataCache")));

        // An empty profile list overrules the record, so the mod goes back to its direct junction.
        new OverlayApplier().Apply(Plan(mod, [], record: ["RuntimeDataCache"]));
        Assert.True(Junction.PointsTo(link, mod.FolderPath));
        Assert.True(Directory.Exists(Path.Combine(link, "RuntimeDataCache")));
    }

    [Fact]
    public void The_default_folder_listing_reads_the_real_mod_folder()
    {
        var mod = MakeMod("Listed");

        var missing = Plan(mod, ["NotThere"]).Entries.Single();
        Assert.Equal(OverlayKind.DirectJunction, missing.Kind);
        Assert.Contains(missing.Notes, n => n.StartsWith(OverlayPlanner.NotLeftOutNote) && n.Contains("NotThere"));

        var present = Plan(mod, null, record: ["runtimedatacache"]).Entries.Single();
        Assert.Equal(OverlayKind.Shadow, present.Kind);
        Assert.Equal(["RuntimeDataCache"], present.ExcludedFolders);
    }
}
