using ModderLords.Core.Modules;

namespace ModderLords.Core.Tests;

/// <summary>
/// These need alternate data streams, which means NTFS. Every assertion is behind a check that the stream we just
/// wrote actually stuck, so the suite still passes on a volume (or a runner) that does not have them.
/// </summary>
public class BlockedFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-blocked-" + Guid.NewGuid().ToString("N"));

    public BlockedFilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Make(string name, bool blocked)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        if (blocked) File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]" + Environment.NewLine + "ZoneId=3");
        return path;
    }

    private bool StreamsWork() => BlockedFiles.IsBlocked(Make("probe.dll", blocked: true));

    [Fact]
    public void Finds_blocked_assemblies_and_ignores_everything_else()
    {
        if (!StreamsWork()) return;
        var dll = Make(Path.Combine("bin", "Win64_Shipping_Client", "Mod.dll"), blocked: true);
        Make(Path.Combine("bin", "Win64_Shipping_Client", "Clean.dll"), blocked: false);
        // A blocked non-assembly is real but harmless: the loader only refuses code.
        Make("SubModule.xml", blocked: true);

        var found = BlockedFiles.Find(_dir);
        Assert.Contains(dll, found);
        Assert.DoesNotContain(found, f => f.EndsWith("Clean.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(found, f => f.EndsWith(".xml", StringComparison.Ordinal));
    }

    [Fact]
    public void Unblocking_clears_the_mark_and_leaves_the_file_alone()
    {
        if (!StreamsWork()) return;
        var dll = Make("Mod.dll", blocked: true);

        Assert.Equal(1, BlockedFiles.UnblockAll([dll]));
        Assert.False(BlockedFiles.IsBlocked(dll));
        Assert.True(File.Exists(dll));
        Assert.Equal("x", File.ReadAllText(dll));
    }

    [Fact]
    public void Finds_nothing_in_a_folder_that_is_not_there()
    {
        Assert.Empty(BlockedFiles.Find(Path.Combine(_dir, "nope")));
        Assert.Empty(BlockedFiles.Find(null));
        Assert.Equal(0, BlockedFiles.UnblockFolder(Path.Combine(_dir, "nope")));
    }
}
