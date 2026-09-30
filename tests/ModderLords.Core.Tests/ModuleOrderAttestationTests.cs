using ModderLords.Operations;
namespace ModderLords.Core.Tests;
public sealed class ModuleOrderAttestationTests
{
    [Fact]
    public void ConnectionInfrastructureMayHaveSideSpecificPlacement()
    {
        Assert.True(ModuleOrderAttestation.Validate(
            ["dependency", "fixture", "ModderLords.Compat", "DedicatedServer.ModderLordsCompat", "CoopNightly"],
            ["Native", "Sandbox", "dependency", "fixture", "CoopNightly", "ModderLords.Compat"], out _));
        Assert.False(ModuleOrderAttestation.Validate(
            ["dependency", "fixture", "ModderLords.Compat", "DedicatedServer.ModderLordsCompat", "CoopNightly"],
            ["Native", "fixture", "dependency", "CoopNightly", "ModderLords.Compat"], out _));
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
