using ModderLords.Core.Overlay;
using ModderLords.Core.Profiles;
using ModderLords.Coop.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// Pins the four launches of 2026-09-19 that found this the expensive way: one module changed at a time, a crash
/// each until the whole BUTR stack was Dependency-only.
/// </summary>
public sealed class ClientStackPolicyTests
{
    private static Profile With(params (string Id, ServerRole Role)[] mods) =>
        new() { Mods = mods.Select(m => new ProfileMod { Id = m.Id, Role = m.Role, Enabled = true }).ToList() };

    [Theory]
    [InlineData("Bannerlord.Harmony")]
    [InlineData("Bannerlord.ButterLib")]
    [InlineData("Bannerlord.MBOptionScreen")]
    public void EachMeasuredCrasherIsRefused(string id)
    {
        Assert.NotNull(ClientStackPolicy.Problem(With((id, ServerRole.AsShipped))));
        Assert.NotNull(ClientStackPolicy.Problem(With((id, ServerRole.Run))));
    }

    [Fact]
    public void DependencyOnlyIsTheSettingThatPasses()
    {
        var profile = With(
            ("Bannerlord.Harmony", ServerRole.DependencyOnly),
            ("Bannerlord.ButterLib", ServerRole.DependencyOnly),
            ("Bannerlord.UIExtenderEx", ServerRole.DependencyOnly),
            ("Bannerlord.MBOptionScreen", ServerRole.DependencyOnly),
            ("RBM", ServerRole.AsShipped));

        Assert.Null(ClientStackPolicy.Problem(profile));
        Assert.Null(ClientStackPolicy.Warning(profile));
    }

    [Fact]
    public void AllThreeAreNamedTogetherRatherThanOneLaunchAtATime()
    {
        var problem = ClientStackPolicy.Problem(With(
            ("Bannerlord.Harmony", ServerRole.AsShipped),
            ("Bannerlord.ButterLib", ServerRole.AsShipped),
            ("Bannerlord.MBOptionScreen", ServerRole.AsShipped)));

        Assert.Contains("Bannerlord.Harmony", problem);
        Assert.Contains("Bannerlord.ButterLib", problem);
        Assert.Contains("Bannerlord.MBOptionScreen", problem);
        Assert.Contains("Import client save", problem);
    }

    /// <summary>Never observed failing, so it warns and must never refuse.</summary>
    [Fact]
    public void UiExtenderWarnsButDoesNotBlock()
    {
        var profile = With(("Bannerlord.UIExtenderEx", ServerRole.AsShipped));
        Assert.Null(ClientStackPolicy.Problem(profile));
        Assert.Contains("WARNING", ClientStackPolicy.Warning(profile));
    }

    [Fact]
    public void ADisabledModIsNotLaunchedAndSoIsNotJudged()
    {
        var profile = With(("Bannerlord.Harmony", ServerRole.AsShipped));
        profile.Mods[0].Enabled = false;
        Assert.Null(ClientStackPolicy.Problem(profile));
    }

    [Fact]
    public void OrdinaryModsAreLeftAlone()
    {
        Assert.Null(ClientStackPolicy.Problem(With(
            ("RBM", ServerRole.AsShipped), ("ModularSmithing2", ServerRole.Run))));
    }
}
