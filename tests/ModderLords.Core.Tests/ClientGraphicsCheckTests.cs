using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// The pre-launch warning for the client terrain crash. The crashing client's engine_config.txt had
/// terrain_quality = 0 and shader_quality = 0.
/// </summary>
public class ClientGraphicsCheckTests
{
    [Fact]
    public void The_crashing_clients_settings_warn_about_both()
    {
        var warning = Assert.Single(ClientGraphicsCheck.Evaluate(["texture_quality = 1", "shader_quality = 0", "terrain_quality = 0"]));
        Assert.Contains("Terrain Quality and Shader Quality are set low", warning);
        Assert.Contains("Compile Shaders", warning);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void Recommended_settings_do_not_warn(int terrain, int shader)
        => Assert.Empty(ClientGraphicsCheck.Evaluate([$"terrain_quality = {terrain}", $"shader_quality = {shader}"]));

    [Fact]
    public void Only_the_setting_that_is_low_is_named()
    {
        var warning = Assert.Single(ClientGraphicsCheck.Evaluate(["terrain_quality = 2", "shader_quality = 1"]));
        Assert.Contains("Shader Quality is set low", warning);
        Assert.DoesNotContain("Terrain Quality", warning.Split('.')[0]);
    }

    [Fact]
    public void Missing_or_unreadable_values_do_not_warn()
    {
        Assert.Empty(ClientGraphicsCheck.Evaluate(["weapon_trail_amount = 0.5000", "garbage"]));
        Assert.Empty(ClientGraphicsCheck.Warnings(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "engine_config.txt")));
    }
}
