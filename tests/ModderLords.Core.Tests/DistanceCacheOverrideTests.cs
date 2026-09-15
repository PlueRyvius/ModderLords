using Bannerlord.ModuleManager;
using ModderLords.Core.Modules;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// The server's settlement distance cache path is hardcoded to SandBox's copy, so a map mod's rebuilt cache is
/// installed and never read. This replaces the file — the only writing this launcher does inside the DedicatedServer
/// package — which is why it is opt-in and why switching it off has to put the original back.
/// </summary>
public class DistanceCacheOverrideTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modderlords-cache").FullName;
    private readonly ServerPaths _paths;

    public DistanceCacheOverrideTests()
        => _paths = ServerPaths.Create(Path.Combine(_root, "DedicatedServer"), Path.Combine(_root, "data"), Path.Combine(_root, "coop"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Vanilla(string content)
    {
        var p = DistanceCacheOverride.TargetPath(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private DiscoveredModule ModWithCache(string id, string content)
    {
        var dir = Path.Combine(_root, "mods", id);
        var file = Path.Combine(dir, DistanceCacheOverride.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return new DiscoveredModule(id, "v2.0.27", dir, ModuleSourceKind.Workshop,
            new ModuleInfoExtended { Id = id, Name = id, Version = ApplicationVersion.Empty });
    }

    private static DiscoveredModule PlainMod(string id) =>
        new(id, "v1.0.0", Path.GetTempPath(), ModuleSourceKind.Workshop,
            new ModuleInfoExtended { Id = id, Name = id, Version = ApplicationVersion.Empty });

    [Fact]
    public void Preflight_refuses_a_map_mod_cache_with_the_override_off()
    {
        var problem = DistanceCacheOverride.PreflightProblem([PlainMod("Bannerlord.Harmony"), ModWithCache("TAOM_Map", "taom")], enabled: false);
        Assert.NotNull(problem);
        Assert.StartsWith("TAOM_Map ships its own settlement distance cache", problem);
        Assert.Contains("Use a map mod's distance cache", problem);
    }

    [Fact]
    public void Preflight_is_quiet_when_the_override_is_on_or_no_mod_ships_a_cache()
    {
        Assert.Null(DistanceCacheOverride.PreflightProblem([ModWithCache("TAOM_Map", "taom")], enabled: true));
        Assert.Null(DistanceCacheOverride.PreflightProblem([PlainMod("MyLittleWarband"), PlainMod("ImprovedGarrisons")], enabled: false));
    }

    [Fact]
    public void Preflight_does_not_count_SandBox_itself_as_a_map_mod()
    {
        Assert.Null(DistanceCacheOverride.PreflightProblem([ModWithCache("SandBox", "vanilla")], enabled: false));
    }

    [Fact]
    public void Off_by_default_leaves_a_normal_server_completely_alone()
    {
        var target = Vanilla("vanilla");
        var r = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: false);

        Assert.False(r.Applied);
        Assert.Equal("vanilla", File.ReadAllText(target));
        Assert.False(File.Exists(DistanceCacheOverride.BackupPath(_paths)));
    }

    [Fact]
    public void Enabled_it_installs_the_mods_cache_and_keeps_the_original()
    {
        var target = Vanilla("vanilla");
        var r = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);

        Assert.True(r.Applied);
        Assert.Equal("TAOM_Map", r.ProviderId);
        Assert.Equal("taom", File.ReadAllText(target));
        Assert.Equal("vanilla", File.ReadAllText(DistanceCacheOverride.BackupPath(_paths)));
    }

    /// <summary>The point of the toggle: a profile that stops using the map mod must go back to a stock server.</summary>
    [Fact]
    public void Turning_it_back_off_restores_the_original()
    {
        var target = Vanilla("vanilla");
        DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);
        var r = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: false);

        Assert.False(r.Applied);
        Assert.Equal("vanilla", File.ReadAllText(target));
        Assert.False(File.Exists(DistanceCacheOverride.BackupPath(_paths)));   // nothing left behind
    }

    /// <summary>Re-applying must never overwrite the backup with the mod's copy, or the original is lost forever.</summary>
    [Fact]
    public void The_backup_is_never_overwritten_by_a_second_apply()
    {
        Vanilla("vanilla");
        for (var i = 0; i < 3; i++)
            DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);

        Assert.Equal("vanilla", File.ReadAllText(DistanceCacheOverride.BackupPath(_paths)));
        DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: false);
        Assert.Equal("vanilla", File.ReadAllText(DistanceCacheOverride.TargetPath(_paths)));
    }

    [Fact]
    public void An_unchanged_setup_does_not_rewrite_the_file()
    {
        var target = Vanilla("vanilla");
        DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);
        var stamp = File.GetLastWriteTimeUtc(target);

        var again = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);
        Assert.True(again.Applied);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public void With_no_provider_it_says_so_and_changes_nothing()
    {
        var target = Vanilla("vanilla");
        var r = DistanceCacheOverride.Sync(_paths, [PlainMod("ModularSmithing2")], enabled: true);

        Assert.False(r.Applied);
        Assert.Equal("vanilla", File.ReadAllText(target));
        Assert.Contains(r.Messages, m => m.Contains("no selected mod ships"));
    }

    /// <summary>Later modules override earlier ones' data; the cache follows the same rule, and says which won.</summary>
    [Fact]
    public void The_last_provider_in_load_order_wins()
    {
        Vanilla("vanilla");
        var r = DistanceCacheOverride.Sync(_paths,
            [ModWithCache("FirstMap", "first"), ModWithCache("TAOM_Map", "taom")], enabled: true);

        Assert.Equal("TAOM_Map", r.ProviderId);
        Assert.Equal("taom", File.ReadAllText(DistanceCacheOverride.TargetPath(_paths)));
        Assert.Contains(r.Messages, m => m.Contains("2 mods ship one"));
    }

    /// <summary>
    /// The failure that actually happened: a previous server process still held the file, the restore failed, and the
    /// next launch of the known-good stack ran against the map mod's cache and died with 0xE0434352. Leaving the wrong
    /// file in place is a broken server, so a restore that cannot complete must stop the launch, not warn.
    /// </summary>
    [Fact]
    public void A_restore_that_cannot_complete_stops_the_launch()
    {
        var target = Vanilla("vanilla");
        DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);

        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))   // stand in for the running engine
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: false));
            Assert.Contains("could not be restored", ex.Message);
            Assert.Contains("would start with a map mod's cache and crash", ex.Message);
        }

        // Once the lock is gone the next launch heals it by itself.
        var after = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: false);
        Assert.Contains(after.Messages, m => m.Contains("restored"));
        Assert.Equal("vanilla", File.ReadAllText(target));
    }

    [Fact]
    public void A_server_without_the_vanilla_file_is_reported_not_crashed()
    {
        var r = DistanceCacheOverride.Sync(_paths, [ModWithCache("TAOM_Map", "taom")], enabled: true);
        Assert.False(r.Applied);
        Assert.Contains(r.Messages, m => m.StartsWith("WARNING") && m.Contains("does not exist"));
    }
}
