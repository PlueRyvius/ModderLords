using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

public class MapIdentityCheckTests
{
    // Real lines, from the run on 2026-09-11 that generated a TAOM world and then served it on the stock map.
    private const string Taom = "[DedicatedServer] Main_map read OK, navmesh CRC=3101457840";
    private const string Vanilla = "[DedicatedServer] Main_map read OK, navmesh CRC=1465536726";

    [Fact]
    public void Reports_nothing_when_the_served_map_is_the_one_the_world_was_built_on()
    {
        var check = new MapIdentityCheck();
        check.ObserveCreation(Taom);
        Assert.Null(check.ObserveServing(Taom));
        Assert.Equal(3101457840u, check.CreatedOn);
        Assert.Equal(3101457840u, check.ServedOn);
    }

    [Fact]
    public void Reports_a_mismatch_naming_both_maps()
    {
        var check = new MapIdentityCheck();
        check.ObserveCreation(Taom);
        var message = check.ObserveServing(Vanilla);
        Assert.NotNull(message);
        Assert.Contains("3101457840", message);
        Assert.Contains("1465536726", message);
    }

    [Fact]
    public void Reports_a_mismatch_only_once_however_often_the_map_is_re_read()
    {
        var check = new MapIdentityCheck();
        check.ObserveCreation(Taom);
        Assert.NotNull(check.ObserveServing(Vanilla));
        // The engine re-reads Main_map per scene load; an earlier run logged it in a tight loop.
        for (var i = 0; i < 50; i++) Assert.Null(check.ObserveServing(Vanilla));
    }

    [Fact]
    public void Says_nothing_when_there_was_no_creation_phase_to_compare_against()
    {
        // Loading an existing save is the ordinary case: there is no "built on" map to disagree with.
        var check = new MapIdentityCheck();
        Assert.Null(check.ObserveServing(Vanilla));
        Assert.Null(check.CreatedOn);
        Assert.Equal(1465536726u, check.ServedOn);
    }

    [Fact]
    public void The_last_creation_read_is_the_one_that_counts()
    {
        // Creation reads the map more than once; the final read is the map the world was actually saved against.
        var check = new MapIdentityCheck();
        check.ObserveCreation(Vanilla);
        check.ObserveCreation(Taom);
        Assert.Null(check.ObserveServing(Taom));
    }

    [Theory]
    [InlineData("nothing to see here")]
    [InlineData("navmesh CRC=")]
    [InlineData("[Coop] Packet profile over 10 seconds (93 bytes/sec avg)")]
    public void Ignores_lines_that_carry_no_crc(string line)
    {
        var check = new MapIdentityCheck();
        check.ObserveCreation(line);
        Assert.Null(check.ObserveServing(line));
        Assert.Null(check.CreatedOn);
        Assert.Null(check.ServedOn);
    }

    [Fact]
    public void Handles_a_crc_above_int_max()
    {
        // TAOM's own CRC does not fit in a signed int; parsing it as one would wrap and compare equal by accident.
        var check = new MapIdentityCheck();
        check.ObserveCreation("navmesh CRC=4294967295");
        Assert.Equal(4294967295u, check.CreatedOn);
        Assert.NotNull(check.ObserveServing(Vanilla));
    }
}
