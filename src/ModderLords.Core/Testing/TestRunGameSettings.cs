using System.Text;
using System.Text.RegularExpressions;
using ModderLords.Core.Smoke;

namespace ModderLords.Core.Testing;

/// <summary>
/// The game's settings for the length of a test run, so the run leaves the PC usable: the player's game opens in a
/// small window instead of over the whole screen, and keeps running while another window has the focus. Both files are
/// put back byte for byte afterwards (<see cref="Restore"/>).
/// <para>
/// engine_config.txt: <c>display_mode = 0</c> at 1280x720. The game's own strings name the values
/// (<c>str_options_type_DisplayMode_N</c> in Native's global_strings.xml): 0 Windowed, 1 Borderless Fullscreen,
/// 2 Fullscreen. BannerlordConfig.txt: <c>StopGameOnFocusLost=False</c>, or the campaign pauses whenever the game
/// is not the window in front, and the steps, which run on the game's tick, stop with it.
/// </para>
/// <para>
/// Coop's automatic join snapshots and restores engine_config.txt too (<see cref="SmokeRunner"/>), inside this one:
/// it saves the test-run settings and puts those back, then this puts back the player's own.
/// </para>
/// </summary>
public sealed class TestRunGameSettings
{
    /// <summary>engine_config.txt's display_mode for a window (Native's str_options_type_DisplayMode_0).</summary>
    public const int Windowed = 0;
    public const int Width = 1280;
    public const int Height = 720;

    private readonly EngineConfigGuard _engine;
    private readonly EngineConfigGuard _game;

    public static string DefaultConfigsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs");

    private TestRunGameSettings(string configsDir)
    {
        _engine = new EngineConfigGuard(Path.Combine(configsDir, "engine_config.txt"));
        _game = new EngineConfigGuard(Path.Combine(configsDir, "BannerlordConfig.txt"));
    }

    /// <summary>Saves both files as they are, then writes the run's settings into them. Returns what was done, for the log.</summary>
    public static TestRunGameSettings Apply(string configsDir, out IReadOnlyList<string> notes)
    {
        var guard = new TestRunGameSettings(configsDir);
        notes =
        [
            Set(guard._engine.Path, " = ", new() { ["display_mode"] = Windowed.ToString(), ["display_width"] = Width.ToString(), ["display_height"] = Height.ToString() },
                $"the game opens in a {Width}x{Height} window"),
            Set(guard._game.Path, "=", new() { ["StopGameOnFocusLost"] = "False" }, "the game keeps running behind other windows"),
        ];
        return guard;
    }

    /// <summary>Puts both files back as they were before <see cref="Apply"/>. Returns what was done, for the log.</summary>
    public IReadOnlyList<string> Restore() => [_engine.Restore(), _game.Restore()];

    /// <summary>
    /// Sets each key's value in a <c>key = value</c> file, adding a key it lacks. A missing file is left alone: the
    /// game writes its defaults on first start, and the run is then simply not windowed.
    /// </summary>
    internal static string Set(string path, string separator, Dictionary<string, string> values, string what)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return $"{name}: not found, so not changed ({what} only once the game has made it)";
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            foreach (var (key, value) in values)
            {
                var line = new Regex(@"^([ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*)[^\r\n]*", RegexOptions.Multiline);
                if (line.IsMatch(text)) text = line.Replace(text, m => m.Groups[1].Value + value);
                else text = text + (text.Length == 0 || text.EndsWith('\n') ? "" : newline) + key + separator + value + newline;
            }
            var attributes = File.GetAttributes(path);
            // Coop's join leaves engine_config.txt read-only; the game runs with whatever is written here.
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.WriteAllBytes(path, [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. new UTF8Encoding(false).GetBytes(text)]);
            File.SetAttributes(path, attributes);
            return $"{name}: {string.Join(", ", values.Select(v => v.Key + "=" + v.Value))} for this run, so {what}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{name}: could not be changed ({ex.Message}), so the game opens with the player's own settings";
        }
    }
}
