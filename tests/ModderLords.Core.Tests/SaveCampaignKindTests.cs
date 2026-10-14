using System.IO.Compression;
using System.Text;
using ModderLords.Core.Saves;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// A Story Mode campaign cannot be hosted and its metadata does not say what it is. Shapes here follow real saves:
/// metadata, then a raw Deflate body whose string table names the campaign behaviours that were running.
/// </summary>
public class SaveCampaignKindTests
{
    private const string StoryHeader =
        """{"List":{"Modules":"Bannerlord.Harmony;Native;SandBoxCore;Sandbox;StoryMode;CustomBattle;Bannerlord.Diplomacy;Coop","Module_Bannerlord.Harmony":"v2.4.2.0","Module_Bannerlord.Diplomacy":"v1.5.4.0"}}""";

    private const string NoStoryModeHeader =
        """{"List":{"Modules":"Bannerlord.Harmony;Native;SandBoxCore;Sandbox;Bannerlord.Diplomacy;Coop","Module_Bannerlord.Harmony":"v2.4.2.0","Module_Bannerlord.Diplomacy":"v1.5.4.0"}}""";

    private static readonly Dictionary<string, string> Planned = new()
    {
        ["Bannerlord.Harmony"] = "v2.4.2", ["Bannerlord.Diplomacy"] = "v1.5.4",
    };

    private static string WriteSave(string json, params string[] behaviours)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sav");
        var header = Encoding.UTF8.GetBytes(json);
        using var fs = File.Create(tmp);
        fs.Write(BitConverter.GetBytes(header.Length));
        fs.Write(header);
        using var body = new DeflateStream(fs, CompressionLevel.Fastest);
        body.Write(new byte[300_000]);                       // object data comes first; the names are at the end
        foreach (var b in behaviours) { body.Write([0, 0, 0]); body.Write(Encoding.ASCII.GetBytes(b)); }
        return tmp;
    }

    private static bool? Kind(string path) => SaveCampaignKind.IsStoryCampaign(SaveHeaderReader.TryRead(path, out _)!);

    [Fact]
    public void A_story_campaign_is_recognised_by_the_behaviours_it_saved()
    {
        var path = WriteSave(StoryHeader, "TownMerchantsCampaignBehavior", "MainStorylineCampaignBehavior", "FirstPhaseCampaignBehavior");
        try { Assert.True(Kind(path)); }
        finally { File.Delete(path); }
    }

    /// <summary>The module is in every save a client with StoryMode enabled writes; it proves nothing by itself.</summary>
    [Fact]
    public void A_sandbox_world_made_with_StoryMode_enabled_is_not_a_story_campaign()
    {
        var path = WriteSave(StoryHeader, "TownMerchantsCampaignBehavior", "XorberaxLegacy.Logic.TrainingFieldCampaignBehavior");
        try
        {
            Assert.False(Kind(path));
            Assert.Empty(SaveModuleCheck.Messages(path, Planned));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_save_without_the_StoryMode_module_is_answered_from_its_header()
    {
        var path = WriteSave(NoStoryModeHeader);
        try
        {
            File.AppendAllText(path, "not deflate");          // the body is never opened, so a bad one cannot matter
            Assert.False(Kind(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_unreadable_body_is_unknown_and_warns_about_nothing()
    {
        var path = WriteSave(StoryHeader);
        try
        {
            using (var fs = File.OpenWrite(path)) { fs.SetLength(fs.Length - 4); fs.Seek(0, SeekOrigin.End); fs.Write([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); }
            Assert.NotEqual(true, Kind(path));
            Assert.Empty(SaveModuleCheck.Messages(path, Planned));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The support-bundle case: save and profile agreed on every community module, and nothing was said.</summary>
    [Fact]
    public void The_launch_is_warned_even_when_every_community_module_agrees()
    {
        var path = WriteSave(StoryHeader, "MainStorylineCampaignBehavior");
        try
        {
            var warning = Assert.Single(SaveModuleCheck.Messages(path, Planned));
            Assert.StartsWith("WARNING", warning);
            Assert.Contains("Story Mode campaign", warning);
            Assert.Contains("Sandbox", warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_story_warning_comes_before_the_module_differences()
    {
        var path = WriteSave(StoryHeader, "MainStorylineCampaignBehavior");
        try
        {
            var messages = SaveModuleCheck.Messages(path, new Dictionary<string, string> { ["Bannerlord.Harmony"] = "v2.4.2" });
            Assert.Equal(2, messages.Count);
            Assert.Contains("Story Mode campaign", messages[0]);
            Assert.Contains("Bannerlord.Diplomacy", messages[1]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>One byte at a time, so every needle is split across reads at every possible point.</summary>
    [Fact]
    public void A_name_split_across_two_reads_is_still_found()
    {
        var needle = Encoding.ASCII.GetBytes("MainStorylineCampaignBehavior");
        var data = new byte[1000].Concat(needle).Concat(new byte[10]).ToArray();
        Assert.True(SaveCampaignKind.ContainsAny(new TrickleStream(data), [needle]));
        Assert.False(SaveCampaignKind.ContainsAny(new TrickleStream(data), [Encoding.ASCII.GetBytes("FirstPhaseCampaignBehavior")]));
    }

    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
}
