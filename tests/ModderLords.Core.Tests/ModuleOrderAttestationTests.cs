using ModderLords.Operations;
namespace ModderLords.Core.Tests;
public sealed class ModuleOrderAttestationTests
{
    [Fact]
    public void PlatformModulesDoNotHideSelectedOrderChanges()
    {
        Assert.True(ModuleOrderAttestation.Validate(["CoopNightly", "fixture"], ["Native", "Sandbox", "CoopNightly", "ModderLords.Compat", "fixture"], out _));
        Assert.False(ModuleOrderAttestation.Validate(["CoopNightly", "fixture"], ["Native", "fixture", "CoopNightly"], out _));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("fixture")]
    public void ExtraAndDuplicateModulesFail(string extra)
        => Assert.False(ModuleOrderAttestation.Validate(["fixture"], ["Native", "fixture", extra], out _));
    [Fact]
    public void MissingSelectedModuleFails()
        => Assert.False(ModuleOrderAttestation.Validate(["fixture"], ["Native", "Sandbox"], out _));
}
