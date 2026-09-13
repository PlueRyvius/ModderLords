namespace ModderLords.Core.Launch;

/// <summary>
/// Warns when this PC's Bannerlord graphics settings are the known trigger for the client terrain crash.
///
/// Measured 2026-09-12: a co-op client crashed loading the vanilla village scene <c>sturgia_village_g</c> for a raid,
/// with <c>rglGPU_device::create_texture_array failed at d3d_device_->CreateTexture2D! The parameter is incorrect.</c>
/// It is the same failure as the field-battle crashes in docs/FIELD-BATTLE-TERRAIN.md, and every crashing scene ships
/// without a terrain shader cache. The client had <c>terrain_quality = 0</c> and <c>shader_quality = 0</c>. The error is
/// a long-standing general Bannerlord report, and the community fix is raising Terrain Quality to Medium or higher,
/// Shader Quality to High, and running Compile Shaders — no mod involved. It is a warning, not a refusal: only the
/// player can change these, and the game rewrites the file on exit.
/// </summary>
public static class ClientGraphicsCheck
{
    public static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "engine_config.txt");

    /// <summary>Reads the config at <paramref name="configPath"/>; no file or an unreadable one yields no warnings.</summary>
    public static IReadOnlyList<string> Warnings(string configPath)
    {
        try { return File.Exists(configPath) ? Evaluate(File.ReadAllLines(configPath)) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The thresholds follow the community guidance: terrain at least the step above the lowest, shader two steps up
    /// (the options menu's "High").
    /// </summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<string> lines)
    {
        var values = Parse(lines);
        var low = new List<string>();
        if (values.TryGetValue("terrain_quality", out var terrain) && terrain < 1) low.Add("Terrain Quality");
        if (values.TryGetValue("shader_quality", out var shader) && shader < 2) low.Add("Shader Quality");
        if (low.Count == 0) return [];
        return [$"Client graphics: {string.Join(" and ", low)} {(low.Count > 1 ? "are" : "is")} set low. That is the known trigger for the " +
                "\"create_texture_array failed at CreateTexture2D\" crash on scenes that ship no terrain shader cache (village raids and some " +
                "battle maps). In Bannerlord's options set Terrain Quality to Medium or higher and Shader Quality to High, then use Compile Shaders."];
    }

    private static Dictionary<string, int> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (int.TryParse(line[(eq + 1)..].Trim(), out var n)) result[line[..eq].Trim()] = n;
        }
        return result;
    }
}
