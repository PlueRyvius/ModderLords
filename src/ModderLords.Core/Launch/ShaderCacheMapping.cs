using System.Text;

namespace ModderLords.Core.Launch;

/// <summary>
/// Does, before a launch, what the game does after its "Mod change detected" prompt, so the prompt never shows.
///
/// The engine (TaleWorlds.Native.dll, not the launcher) keeps the module set its runtime shader cache was built for in
/// %ProgramData%\Mount and Blade II Bannerlord\Shaders\CoreShaders\&lt;api&gt;\shader_mapping.bin. When the game starts
/// with a different set it shows a Win32 message box, "A change in your active mods was detected. Deleting the runtime
/// shader cache to avoid rendering issues.", whose only button is OK, and loads nothing more until someone presses it.
/// It then deletes the other files in that folder and writes the new set. An unattended test run stalled there.
///
/// VERIFIED 2026-11-08 against v1.4.8.119303: the file is a uint32 format (0x783), the game build and the module ids
/// (each an int32 length then the bytes), the ids sorted ordinally and joined with ';'. Starting the game with another
/// set showed the box 1.6 s in; pressing OK removed a sentinel file from D3D11, rewrote the mapping, and left
/// TerrainShaders alone. See docs/INGAME-TEST-HARNESS.md.
/// </summary>
public static class ShaderCacheMapping
{
    public const string FileName = "shader_mapping.bin";

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Mount and Blade II Bannerlord", "Shaders", "CoreShaders");

    public sealed record Mapping(uint Format, string Build, string Modules);

    /// <summary>The module list as the engine stores it: ids sorted ordinally, joined with ';'.</summary>
    public static string ModulesKey(IEnumerable<string> moduleIds) => string.Join(";", moduleIds.Order(StringComparer.Ordinal));

    /// <summary>Null for anything that is not exactly the layout above, so a changed format is left to the game.</summary>
    public static Mapping? Parse(byte[] bytes)
    {
        var at = 0;
        if (!UInt(out var format) || !Text(out var build) || !Text(out var modules) || at != bytes.Length) return null;
        return new Mapping(format, build, modules);

        bool UInt(out uint value)
        {
            value = 0;
            if (bytes.Length - at < 4) return false;
            value = BitConverter.ToUInt32(bytes, at);
            at += 4;
            return true;
        }
        bool Text(out string value)
        {
            value = "";
            if (!UInt(out var length) || length > bytes.Length - at) return false;
            value = Encoding.UTF8.GetString(bytes, at, (int)length);
            at += (int)length;
            return true;
        }
    }

    public static byte[] Serialize(Mapping mapping)
    {
        var build = Encoding.UTF8.GetBytes(mapping.Build);
        var modules = Encoding.UTF8.GetBytes(mapping.Modules);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(mapping.Format);
            writer.Write(build.Length);
            writer.Write(build);
            writer.Write(modules.Length);
            writer.Write(modules);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// For every renderer folder that has a mapping for another module set: deletes the other files in that folder
    /// (the runtime shader cache the game would delete) and records <paramref name="moduleIds"/>, keeping the format
    /// and build as they were. Says what it did, or null when nothing needed doing. Never throws: on any failure the
    /// game simply asks, as it always has.
    /// </summary>
    public static string? Prepare(IEnumerable<string> moduleIds, string? root = null)
    {
        root ??= DefaultRoot;
        var key = ModulesKey(moduleIds);
        var notes = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) continue;
                var mapping = Parse(File.ReadAllBytes(path));
                if (mapping is null) { notes.Add($"{path} is not in a layout ModderLords knows; the game may ask about changed mods"); continue; }
                if (mapping.Modules == key) continue;
                var cleared = 0;
                foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(file);
                    cleared++;
                }
                var temp = path + ".modderlords";
                File.WriteAllBytes(temp, Serialize(mapping with { Modules = key }));
                File.Move(temp, path, overwrite: true);
                notes.Add($"the mod set changed since the game last ran: cleared {cleared} cached shader file(s) in {folder} and recorded the new set, " +
                          "as the game does after its \"Mod change detected\" prompt");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("could not prepare the shader cache for the new mod set (" + ex.Message + "); the game will ask about changed mods");
        }
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }
}
