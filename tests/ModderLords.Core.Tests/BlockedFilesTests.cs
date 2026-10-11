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

    private static ModderLords.Core.Launch.ClientLaunchPlan PlanWith(params (string Id, string Folder)[] mods) => new()
    {
        GameRoot = "G", ModuleIds = mods.Select(m => m.Id).ToList(),
        SelectedModules = mods.Select(m => new DiscoveredModule(m.Id, "v1.0.0", m.Folder, ModuleSourceKind.Custom,
            new Bannerlord.ModuleManager.ModuleInfoExtended { Id = m.Id, Name = m.Id })).ToList(),
    };

    [Fact]
    public void A_launch_unblocks_the_mods_it_is_about_to_load_and_says_which()
    {
        if (!StreamsWork()) return;
        // The copy the launch uses, somewhere the player unzipped it. The game would name it by its path inside the
        // launch view, which is a link to this folder.
        var dll = Make(Path.Combine("Unzipped", "SomeMod", "bin", "Win64_Shipping_Client", "SomeMod.dll"), blocked: true);
        var clean = Make(Path.Combine("Unzipped", "Other", "bin", "Win64_Shipping_Client", "Other.dll"), blocked: false);

        var said = ModderLords.Core.Launch.ClientLaunchSession.Unblock(PlanWith(
            ("SomeMod", Path.Combine(_dir, "Unzipped", "SomeMod")), ("Other", Path.Combine(_dir, "Unzipped", "Other"))));

        Assert.False(BlockedFiles.IsBlocked(dll));
        Assert.Equal("x", File.ReadAllText(dll));
        Assert.True(File.Exists(clean));
        Assert.Contains("unblocked 1 file(s)", said);
        Assert.Contains("SomeMod", said);
        Assert.DoesNotContain("Other", said);
    }

    [Fact]
    public void A_launch_with_nothing_blocked_says_nothing()
    {
        Make(Path.Combine("Mod", "bin", "Win64_Shipping_Client", "Mod.dll"), blocked: false);
        Assert.Null(ModderLords.Core.Launch.ClientLaunchSession.Unblock(PlanWith(("Mod", Path.Combine(_dir, "Mod")), ("Gone", Path.Combine(_dir, "nope")))));
    }
}