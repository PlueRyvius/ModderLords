using ModderLords.Core.Testing;

namespace ModderLords.Core.Tests;

public sealed class TestRunGameSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("test-run-settings").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string Engine => Path.Combine(_dir, "engine_config.txt");
    private string Game => Path.Combine(_dir, "BannerlordConfig.txt");

    [Fact]
    public void RunsWindowedAndUnpausedThenPutsBothFilesBackByteForByte()
    {
        var engine = "texture_quality = 1\r\ndisplay_width = 1920\r\ndisplay_height = 1080\r\ndisplay_mode = 1\r\nsafely_exited  = 1\r\n"u8.ToArray();
        var game = "Language=English\r\nStopGameOnFocusLost=True\r\nLatestSaveGameName=mine\r\n"u8.ToArray();
        File.WriteAllBytes(Engine, engine);
        File.WriteAllBytes(Game, game);
        // Coop's join leaves engine_config.txt read-only; the run must still change it, and leave it read-only.
        File.SetAttributes(Engine, FileAttributes.ReadOnly);

        var settings = TestRunGameSettings.Apply(_dir, out var notes);
        Assert.Equal(2, notes.Count);
        Assert.Equal("texture_quality = 1\r\ndisplay_width = 1280\r\ndisplay_height = 720\r\ndisplay_mode = 0\r\nsafely_exited  = 1\r\n", File.ReadAllText(Engine));
        Assert.Equal("Language=English\r\nStopGameOnFocusLost=False\r\nLatestSaveGameName=mine\r\n", File.ReadAllText(Game));
        Assert.True(File.GetAttributes(Engine).HasFlag(FileAttributes.ReadOnly));

        // What the game does during a run: saves its own settings.
        File.SetAttributes(Engine, FileAttributes.Normal);
        File.WriteAllText(Engine, "display_mode = 0\r\nsafely_exited  = 0\r\n");
        File.WriteAllText(Game, "LatestSaveGameName=mltest-harness\r\n");

        settings.Restore();
        Assert.Equal(engine, File.ReadAllBytes(Engine));
        Assert.Equal(game, File.ReadAllBytes(Game));
        Assert.True(File.GetAttributes(Engine).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void AddsAKeyTheFileLacks()
    {
        File.WriteAllText(Game, "Language=English");
        File.WriteAllText(Engine, "display_mode = 2\n");
        TestRunGameSettings.Apply(_dir, out _);
        Assert.Equal("Language=English\nStopGameOnFocusLost=False\n", File.ReadAllText(Game));
        Assert.Equal("display_mode = 0\ndisplay_width = 1280\ndisplay_height = 720\n", File.ReadAllText(Engine));
    }

    [Fact]
    public void MissingFilesAreLeftMissing()
    {
        var settings = TestRunGameSettings.Apply(_dir, out var notes);
        Assert.All(notes, n => Assert.Contains("not found", n));
        settings.Restore();
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
