using ModderLords.Coop.Launch;

namespace ModderLords.Core.Tests;

public sealed class TaomLaunchPolicyTests
{
    [Fact]
    public void VanillaProfilesAreUnchanged()
    {
        Assert.Null(TaomLaunchPolicy.MessageFor(["Native", "CoopNightly"], null, _ => false));
    }

    [Fact]
    public void TaomRequiresAnExistingCampaign()
    {
        var message = TaomLaunchPolicy.MessageFor(["TAOM", "TAOM_Map", "LOTRLOME_Armory"], "new_world", _ => false);
        Assert.Contains("was not found", message);
        Assert.Contains("Import client save", message);
    }

    [Fact]
    public void BlankTaomSaveIsRejectedWithoutTouchingTheFileSystem()
    {
        var called = false;
        var message = TaomLaunchPolicy.MessageFor(["TAOM", "TAOM_Map", "LOTRLOME_Armory"], "", _ => { called = true; return true; });
        Assert.Contains("needs a campaign save", message);
        Assert.False(called);
    }

    [Fact]
    public void ExistingTaomCampaignIsAllowed()
    {
        Assert.Null(TaomLaunchPolicy.MessageFor(["TAOM", "TAOM_Map", "LOTRLOME_Armory"], "taom_campaign", _ => true));
    }

    [Fact]
    public void MapAndArmoryWithoutTaomAreRejected()
    {
        var message = TaomLaunchPolicy.MessageFor(["TAOM_Map", "LOTRLOME_Armory"], "taom_campaign", _ => true);
        Assert.Contains("TAOM", message);
        Assert.False(TaomLaunchPolicy.HasCompleteRecipe(["TAOM_Map", "LOTRLOME_Armory"]));
    }

    private static string MakeModule(string root, string id, params string[] filledDirs)
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        foreach (var d in filledDirs)
        {
            Directory.CreateDirectory(Path.Combine(folder, d));
            File.WriteAllText(Path.Combine(folder, d, "x.xml"), "<x/>");
        }
        return folder;
    }

    [Fact]
    public void CompleteTaomInstallHasNoProblems()
    {
        var root = Directory.CreateTempSubdirectory("taom-ok").FullName;
        try
        {
            var mods = new[]
            {
                ("TAOM", MakeModule(root, "TAOM", "ModuleData", "GUI")),
                ("TAOM.Dependencies", MakeModule(root, "TAOM.Dependencies", "ModuleData")),
                ("TAOM_Map", MakeModule(root, "TAOM_Map", "ModuleData")),
                ("LOTRLOME_Armory", MakeModule(root, "LOTRLOME_Armory", "ModuleData")),
            };
            Assert.Empty(TaomLaunchPolicy.InstallProblems(mods));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HollowedTaomInstallIsNamed()
    {
        // The 2026-09-22 shape: folders present but emptied, binaries left.
        var root = Directory.CreateTempSubdirectory("taom-hollow").FullName;
        try
        {
            var taom = MakeModule(root, "TAOM");
            Directory.CreateDirectory(Path.Combine(taom, "ModuleData"));
            Directory.CreateDirectory(Path.Combine(taom, "GUI"));
            var problems = TaomLaunchPolicy.InstallProblems([("TAOM", taom), ("Native", MakeModule(root, "Native"))]);
            var line = Assert.Single(problems);
            Assert.Contains("ModuleData and GUI", line);
            Assert.Contains("Reinstall TAOM", line);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TaomWithoutSettingsSyncIsRefused()
    {
        Assert.NotNull(TaomLaunchPolicy.SyncProblem(["TAOM", "TAOM_Map"], settingsSync: false));
        Assert.Null(TaomLaunchPolicy.SyncProblem(["TAOM", "TAOM_Map"], settingsSync: true));
        Assert.Null(TaomLaunchPolicy.SyncProblem(["Native", "ImprovedGarrisons"], settingsSync: false));
    }
}
