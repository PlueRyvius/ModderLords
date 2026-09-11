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
        var message = TaomLaunchPolicy.MessageFor(["TAOM", "TAOM_Map"], "new_world", _ => false);
        Assert.Contains("was not found", message);
        Assert.Contains("Import client save", message);
    }

    [Fact]
    public void BlankTaomSaveIsRejectedWithoutTouchingTheFileSystem()
    {
        var called = false;
        var message = TaomLaunchPolicy.MessageFor(["TAOM"], "", _ => { called = true; return true; });
        Assert.Contains("needs a campaign save", message);
        Assert.False(called);
    }

    [Fact]
    public void ExistingTaomCampaignIsAllowed()
    {
        Assert.Null(TaomLaunchPolicy.MessageFor(["TAOM", "TAOM_Map"], "taom_campaign", _ => true));
    }
}
