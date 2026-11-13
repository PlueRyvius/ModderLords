namespace ModderLords.Core.Smoke;

/// <summary>
/// Puts one of the player's game config files back as it was: engine_config.txt after an automatic join, and both it
/// and BannerlordConfig.txt after a test run (<see cref="Testing.TestRunGameSettings"/>).
/// <para>
/// Coop's <c>/autoconnect</c> rewrites Documents\Mount and Blade II Bannerlord\Configs\engine_config.txt
/// (<c>safely_exited = 1</c>, so the game skips its safe-mode question) and then marks it read-only. Left that way, every
/// graphics or sound setting the player changes afterwards silently stops being saved.
/// </para>
/// </summary>
public sealed class EngineConfigGuard
{
    private readonly byte[]? _content;
    private readonly FileAttributes _attributes;

    public string Path { get; }
    private string Name => System.IO.Path.GetFileName(Path);

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs", "engine_config.txt");

    public EngineConfigGuard(string path)
    {
        Path = path;
        if (!File.Exists(path)) return;
        _content = File.ReadAllBytes(path);
        _attributes = File.GetAttributes(path);
    }

    /// <summary>Writes the original back with its original attributes. Returns what was done, for the report.</summary>
    public string Restore()
    {
        try
        {
            if (_content is null)
            {
                // Nothing to restore, but never leave behind a read-only file the game created during the run.
                if (File.Exists(Path) && File.GetAttributes(Path).HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(Path, File.GetAttributes(Path) & ~FileAttributes.ReadOnly);
                    return Name + ": made writable again";
                }
                return Name + ": not present before the test";
            }
            if (File.Exists(Path)) File.SetAttributes(Path, FileAttributes.Normal);
            File.WriteAllBytes(Path, _content);
            File.SetAttributes(Path, _attributes);
            return Name + ": restored as it was before the test";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Name + ": could not be restored (" + ex.Message + "); if it is read-only, clear that in its Properties";
        }
    }
}
