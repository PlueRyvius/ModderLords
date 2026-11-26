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

    private static readonly string[] FileNames = ["engine_config.txt", "BannerlordConfig.txt"];
    private const string AttributesSuffix = ".attributes";
    private const string OwnerFile = "configs-folder.txt";

    private readonly EngineConfigGuard _engine;
    private readonly EngineConfigGuard _game;
    private readonly string _backupDir;
    private bool _ownsBackup;

    public static string DefaultConfigsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs");

    /// <summary>
    /// Where the player's own files are copied for the length of a run. A run that is killed never reaches
    /// <see cref="Restore"/>, so the next <see cref="Apply"/> finds the copies here and puts them back first.
    /// </summary>
    public static string DefaultBackupDir => Path.Combine(Profiles.ProfileStore.RootDir, "test-run-settings-backup");

    private TestRunGameSettings(string configsDir, string backupDir)
    {
        _engine = new EngineConfigGuard(Path.Combine(configsDir, FileNames[0]));
        _game = new EngineConfigGuard(Path.Combine(configsDir, FileNames[1]));
        _backupDir = backupDir;
    }

    /// <summary>
    /// Saves both files as they are, in memory and in <paramref name="backupDir"/>, then writes the run's settings into
    /// them. If an earlier run was stopped before it put the player's files back, those are restored first. Returns what
    /// was done, for the log.
    /// </summary>
    public static TestRunGameSettings Apply(string configsDir, out IReadOnlyList<string> notes, string? backupDir = null)
    {
        // One folder per Configs folder, so a run against another one (a test's) never touches the player's copies.
        backupDir ??= Path.Combine(DefaultBackupDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(configsDir).ToUpperInvariant())))[..12]);
        var recovered = RecoverLeftOver(configsDir, backupDir);
        var guard = new TestRunGameSettings(configsDir, backupDir);
        var saved = guard.SaveBackup();
        notes =
        [
            .. recovered,
            .. saved,
            Set(guard._engine.Path, " = ", new() { ["display_mode"] = Windowed.ToString(), ["display_width"] = Width.ToString(), ["display_height"] = Height.ToString() },
                $"the game opens in a {Width}x{Height} window"),
            Set(guard._game.Path, "=", new() { ["StopGameOnFocusLost"] = "False" }, "the game keeps running behind other windows"),
        ];
        return guard;
    }

    /// <summary>Puts both files back as they were before <see cref="Apply"/>. Returns what was done, for the log.</summary>
    public IReadOnlyList<string> Restore()
    {
        IReadOnlyList<string> done = [_engine.Restore(), _game.Restore()];
        // The files are back, so the copy on disk is no longer needed; a later Restore (a game kept open) uses memory.
        try { if (_ownsBackup && Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return done;
    }

    /// <summary>Copies the player's files (and their attributes) to the backup folder. Returns a note only on failure.</summary>
    private IReadOnlyList<string> SaveBackup()
    {
        // Still there after RecoverLeftOver: it holds copies that could not be put back, which must not be overwritten.
        if (Directory.Exists(_backupDir)) return [$"your game settings were not copied to {_backupDir}, which still holds an earlier run's; they are put back at the end of the run, but not if the run is killed"];
        try
        {
            _ownsBackup = true;
            Directory.CreateDirectory(_backupDir);
            File.WriteAllText(Path.Combine(_backupDir, OwnerFile), Path.GetFullPath(Path.GetDirectoryName(_engine.Path)!));
            foreach (var path in new[] { _engine.Path, _game.Path })
            {
                if (!File.Exists(path)) continue;
                var target = Path.Combine(_backupDir, Path.GetFileName(path));
                File.WriteAllBytes(target, File.ReadAllBytes(path));
                File.WriteAllText(target + AttributesSuffix, ((int)File.GetAttributes(path)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"your game settings could not be copied to {_backupDir} ({ex.Message}); they are put back at the end of the run, but not if the run is killed"];
        }
    }

    /// <summary>Puts back the player's files that a stopped run left in the backup folder, then removes the folder.</summary>
    private static IReadOnlyList<string> RecoverLeftOver(string configsDir, string backupDir)
    {
        if (!Directory.Exists(backupDir)) return [];
        // Only copies taken from this same Configs folder are put back here.
        var owner = Path.Combine(backupDir, OwnerFile);
        if (!File.Exists(owner) || !string.Equals(File.ReadAllText(owner).Trim(), Path.GetFullPath(configsDir), StringComparison.OrdinalIgnoreCase))
            return [$"an earlier stopped test run left copies of another Configs folder's settings in {backupDir}; they were left there"];
        var notes = new List<string>();
        foreach (var name in FileNames)
        {
            var copy = Path.Combine(backupDir, name);
            if (!File.Exists(copy)) continue;
            var path = Path.Combine(configsDir, name);
            try
            {
                var attributes = File.Exists(copy + AttributesSuffix)
                    && int.TryParse(File.ReadAllText(copy + AttributesSuffix), System.Globalization.CultureInfo.InvariantCulture, out var a)
                    ? (FileAttributes)a : FileAttributes.Normal;
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                File.WriteAllBytes(path, File.ReadAllBytes(copy));
                File.SetAttributes(path, attributes);
                notes.Add($"{name}: put back as it was before an earlier test run that was stopped before it could restore it");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the copy: removing it would lose the player's settings for good.
                notes.Add($"{name}: an earlier stopped test run left your copy in {backupDir}, but it could not be put back ({ex.Message})");
                return notes;
            }
        }
        try { Directory.Delete(backupDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return notes;
    }

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
