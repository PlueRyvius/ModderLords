using System.Text;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Tests;

/// <summary>
/// The fresh-world half of <see cref="SaveModuleCheck"/>: a save that does not exist yet is about to be stamped out
/// of the vanilla template, so the mismatch is knowable before anything is written. Regression cover for the launch
/// of 2026-09-19, where a brand new world under a 12-module load order was reported as mismatched only after the
/// file had been created, and not at all under --dry-run.
/// </summary>
public class FreshWorldModuleCheckTests
{
    private const string VanillaTemplate =
        """{"List":{"Modules":"Native;SandBoxCore;Sandbox;Coop","Module_Native":"v1.4.8.118999","Module_Coop":"v0.1.5.0","ApplicationVersion":"v1.4.8.118999","CharacterName":"","MainHeroLevel":"1","DayLong":"0"}}""";

    private static string WriteSave(string dir, string name, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".sav");
        var body = Encoding.UTF8.GetBytes(json);
        using var fs = File.Create(path);
        fs.Write(BitConverter.GetBytes(body.Length));
        fs.Write(body);
        fs.Write(new byte[64]); // fake payload
        return path;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Missing_save_reports_the_modules_the_template_lacks()
    {
        var dir = NewDir();
        try
        {
            var template = WriteSave(dir, SaveHeaderReader.TemplateSaveName, VanillaTemplate);
            var planned = new Dictionary<string, string> { ["DismembermentPlus"] = "v2.0.8.8", ["ImprovedGarrisons"] = "v1.0.0" };

            var messages = SaveModuleCheck.MessagesForLaunch(dir, "brand-new", planned, template);

            var only = Assert.Single(messages);
            Assert.Contains("does not exist and will be created from", only);
            Assert.Contains("DismembermentPlus", only);
            Assert.Contains("ImprovedGarrisons", only);
            // The whole point of the message is that it names an action; "create a world properly" is not one.
            Assert.Contains("import-save", only);
            Assert.Contains("create-world", only);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_save_is_silent_for_a_vanilla_load_order()
    {
        var dir = NewDir();
        try
        {
            var template = WriteSave(dir, SaveHeaderReader.TemplateSaveName, VanillaTemplate);
            Assert.Empty(SaveModuleCheck.MessagesForLaunch(dir, "brand-new", new Dictionary<string, string>(), template));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void No_template_means_no_fresh_world_claim()
    {
        var dir = NewDir();
        try
        {
            var planned = new Dictionary<string, string> { ["DismembermentPlus"] = "v2.0.8.8" };
            Assert.Empty(SaveModuleCheck.MessagesForLaunch(dir, "brand-new", planned, templatePath: null));
            Assert.Empty(SaveModuleCheck.MessagesForLaunch(dir, "brand-new", planned, Path.Combine(dir, "absent.sav")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_existing_save_is_still_compared_against_itself_not_the_template()
    {
        var dir = NewDir();
        try
        {
            var template = WriteSave(dir, SaveHeaderReader.TemplateSaveName, VanillaTemplate);
            WriteSave(dir, "played", """{"List":{"Modules":"Native;SandBoxCore;Sandbox;Coop;DismembermentPlus","Module_DismembermentPlus":"v2.0.8.8"}}""");

            var planned = new Dictionary<string, string> { ["DismembermentPlus"] = "v2.0.8.8" };
            var messages = SaveModuleCheck.MessagesForLaunch(dir, "played", planned, template);

            // The save agrees with the launch. If the template were consulted instead, this would warn.
            Assert.Empty(messages);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unnamed_save_still_gets_the_autosave_note()
    {
        var dir = NewDir();
        try
        {
            var template = WriteSave(dir, SaveHeaderReader.TemplateSaveName, VanillaTemplate);
            var planned = new Dictionary<string, string> { ["DismembermentPlus"] = "v2.0.8.8" };

            var only = Assert.Single(SaveModuleCheck.MessagesForLaunch(dir, null, planned, template));

            Assert.Contains(SaveModuleCheck.CoopAutoSaveName, only);
            Assert.Contains("no save was named", only);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class MapModVersionWarningTests
{
    [Fact]
    public void MapModVersionChangeIsAWarningThatStillLaunches()
    {
        var line = ModderLords.Core.Saves.SaveModuleCheck.VersionChangeLine("taom_world", "TAOM_Map", "v2.0.27", "v2.0.28", isMapMod: true);
        Assert.StartsWith("WARNING", line);
        Assert.Contains("create a new world", line);
        Assert.Contains("Launching anyway", line);
    }

    [Fact]
    public void OtherVersionChangesStayPlain()
    {
        var line = ModderLords.Core.Saves.SaveModuleCheck.VersionChangeLine("taom_world", "TAOM", "v2.0.27", "v2.0.28", isMapMod: false);
        Assert.DoesNotContain("WARNING", line);
        Assert.Equal("save 'taom_world': TAOM was v2.0.27, this launch has v2.0.28", line);
    }
}
