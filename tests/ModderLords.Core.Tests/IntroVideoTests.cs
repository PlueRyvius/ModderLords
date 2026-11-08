using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

public class IntroVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-intro-" + Guid.NewGuid().ToString("N"));
    private string GameRoot => Path.Combine(_dir, "game");
    private string Ledger => Path.Combine(_dir, "data", "hidden-intro-video.txt");
    private string Video => IntroVideo.VideoPath(GameRoot);
    private string Hidden => Video + IntroVideo.Suffix;

    public IntroVideoTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Video)!);
        File.WriteAllText(Video, "logo");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Hides_the_video_for_a_launch_and_puts_it_back()
    {
        Assert.Null(IntroVideo.Hide(GameRoot, Ledger));
        Assert.False(File.Exists(Video));
        Assert.Equal("logo", File.ReadAllText(Hidden));
        Assert.Equal([Video], File.ReadAllLines(Ledger));

        IntroVideo.RestoreAll(Ledger);
        Assert.Equal("logo", File.ReadAllText(Video));
        Assert.False(File.Exists(Hidden));
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public void A_second_launch_while_hidden_changes_nothing()
    {
        IntroVideo.Hide(GameRoot, Ledger);
        Assert.Null(IntroVideo.Hide(GameRoot, Ledger));
        Assert.Single(File.ReadAllLines(Ledger));
        Assert.True(File.Exists(Hidden));
    }

    [Fact]
    public void A_file_Steam_put_back_wins_over_the_hidden_copy()
    {
        IntroVideo.Hide(GameRoot, Ledger);
        File.WriteAllText(Video, "updated");

        IntroVideo.RestoreAll(Ledger);
        Assert.Equal("updated", File.ReadAllText(Video));
        Assert.False(File.Exists(Hidden));

        // And hiding again replaces the stale copy rather than failing on it.
        File.WriteAllText(Hidden, "stale");
        Assert.Null(IntroVideo.Hide(GameRoot, Ledger));
        Assert.Equal("updated", File.ReadAllText(Hidden));
    }

    [Fact]
    public void No_video_and_no_ledger_are_both_fine()
    {
        File.Delete(Video);
        Assert.Null(IntroVideo.Hide(GameRoot, Ledger));
        Assert.False(File.Exists(Ledger));
        IntroVideo.RestoreAll(Ledger);
    }
}
