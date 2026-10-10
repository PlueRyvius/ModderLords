using ModderLords.Core.Logs;

namespace ModderLords.Core.Tests;

/// <summary>Lines are the real ones, from a Coop 0.1.6 launch on 2026-10-10 that printed 686 of the first kind in 0.2 s.</summary>
public class StartupNoiseFolderTests
{
    private static string ClanMiss(string clan) =>
        $"[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.Clan\", \"{clan}\"";

    private const string Serving = "[DedicatedServer] SERVING — coop server up, waiting for clients";

    [Fact]
    public void The_clan_burst_from_a_save_load_becomes_one_counted_line_when_the_server_serves()
    {
        var f = new StartupNoiseFolder();
        Assert.False(f.Observe("[DedicatedServer]   weather behavior unregistered").Folded);
        foreach (var clan in new[] { "looters", "clan_empire_west_2", "Player" })
        {
            var v = f.Observe(ClanMiss(clan));
            Assert.True(v.Folded);
            Assert.Empty(v.Summaries);
        }

        var serving = f.Observe(Serving);
        Assert.False(serving.Folded);
        var summary = Assert.Single(serving.Summaries);
        Assert.StartsWith("[ModderLords] folded 3 Coop lines", summary);
        Assert.Contains("launch log", summary);
    }

    [Fact]
    public void The_same_line_once_the_server_is_serving_is_shown()
    {
        var f = new StartupNoiseFolder();
        f.Observe(ClanMiss("looters"));
        f.Observe(Serving);

        var later = f.Observe(ClanMiss("clan_vlandia_3"));
        Assert.False(later.Folded);
        Assert.Empty(later.Summaries);
        Assert.Equal(1, f.ClanIdLines);
    }

    [Theory]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.Hero\", \"lord_1_1\"")]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get id for object of type \"TaleWorlds.CampaignSystem.StanceLink\"")]
    [InlineData("[Coop] [\"ObjectManager\"] Failed to get object of type \"TaleWorlds.CampaignSystem.Clan\" with id \"x\"")]
    public void A_failed_lookup_that_was_not_traced_to_the_save_load_is_shown(string line) =>
        Assert.False(new StartupNoiseFolder().Observe(line).Folded);

    [Fact]
    public void Coops_own_schema_complaints_fold_with_their_detail_lines()
    {
        var f = new StartupNoiseFolder();
        Assert.False(f.Observe(@"[DedicatedServer Engine] opening ..\..\Modules\Coop/ModuleData/coop_fixed_town_npcs.xml").Folded);
        Assert.True(f.Observe("[DedicatedServer Engine] Error: The 'coop_settlement' attribute is not declared.").Folded);
        Assert.True(f.Observe("Node: coop_settlement  Value: town_S7").Folded);
        Assert.True(f.Observe("Line: 60").Folded);
        Assert.True(f.Observe("[DedicatedServer Engine] Error: The 'coop_dialogue' attribute is not declared.").Folded);
        Assert.False(f.Observe("[DedicatedServer Engine] loading banner_icons.xml:").Folded);
        // Not directly under a folded complaint: an ordinary line.
        Assert.False(f.Observe("Line: 12").Folded);

        Assert.Contains(f.Observe(Serving).Summaries, s => s.Contains("folded 2 schema complaints"));
    }

    [Fact]
    public void A_schema_error_in_any_other_mods_file_is_shown_with_its_details()
    {
        var f = new StartupNoiseFolder();
        Assert.False(f.Observe("[DedicatedServer Engine] Error: The 'upgrade_requires' attribute is not declared.").Folded);
        Assert.False(f.Observe("Node: upgrade_requires  Value: x").Folded);
        Assert.False(f.Observe("Line: 4").Folded);
    }

    [Fact]
    public void A_launch_that_never_serves_still_reports_what_was_folded_once()
    {
        var f = new StartupNoiseFolder();
        f.Observe(ClanMiss("looters"));
        Assert.Single(f.Report());
        Assert.Empty(f.Report());
    }

    [Fact]
    public void A_clean_load_reports_nothing()
    {
        var f = new StartupNoiseFolder();
        Assert.Empty(f.Observe(Serving).Summaries);
        Assert.Empty(f.Report());
    }

    [Theory]
    [InlineData("[ModderLords.Hook] first-chance System.Net.Http.HttpRequestException: No connection could be made (localhost:5341)")]
    [InlineData("[ModderLords.Hook] first-chance     at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error, CancellationToken cancellationToken)")]
    [InlineData("[ModderLords.Hook] first-chance   inner-> System.Net.Sockets.SocketException: No connection could be made")]
    public void Every_line_of_a_handled_exception_the_hook_prints_is_ours_not_an_error(string line) =>
        Assert.Equal(LogCategory.Tool, LogClassifier.Classify(line).Category);
}
