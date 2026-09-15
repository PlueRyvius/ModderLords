namespace ModderLords.Core.Compat;

/// <summary>
/// Which scenes would crash a client's terrain renderer: a scene folder shipped without its compiled terrain shader
/// cache (<c>SceneObj\&lt;scene&gt;\ShaderCache\D3D11\compressed_shader_cache.sack</c>; see docs/FIELD-BATTLE-TERRAIN.md).
/// Vanilla SandBoxCore ships two battle scenes that way (battle_terrain_020, battle_terrain_a) and one village
/// (sturgia_village_g). A module later in the load order overrides an earlier module's copy of a scene, so the copy
/// that counts is the last one in order that has the folder. The result rides in recipes.json to the server, which
/// keeps Coop from choosing those scenes for a field battle (the server's choice is what every client loads).
/// </summary>
public static class BattleSceneCache
{
    public const string SackRelativePath = @"ShaderCache\D3D11\compressed_shader_cache.sack";

    /// <summary>Scene ids whose effective copy (last module in <paramref name="moduleFoldersInLoadOrder"/> that ships it) has no shader sack. Sorted.</summary>
    public static IReadOnlyList<string> Scan(IEnumerable<string> moduleFoldersInLoadOrder)
    {
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // scene -> folder
        foreach (var module in moduleFoldersInLoadOrder)
        {
            var sceneObj = Path.Combine(module, "SceneObj");
            if (!Directory.Exists(sceneObj)) continue;
            IEnumerable<string> scenes;
            try { scenes = Directory.EnumerateDirectories(sceneObj); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var dir in scenes) effective[Path.GetFileName(dir)] = dir;
        }
        return effective.Where(kv => !File.Exists(Path.Combine(kv.Value, SackRelativePath)))
            .Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
