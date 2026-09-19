using ModderLords.Core.Compat;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Desktop-framework detection. The dedicated server's bundled runtime ships only Microsoft.NETCore.App, so a mod
/// referencing WinForms or WPF cannot even have the referencing method compiled headless — the failure is
/// unconditional, unlike a view-assembly reference that only bites if a headless code path reaches it.
///
/// Regression cover for DismembermentPlus killing the server on 2026-09-19: its OnSubModuleLoad carries a
/// System.Windows.Forms.MessageBox.Show call site inside a catch block that never ran.
/// </summary>
public class DesktopFrameworkScanTests
{
    private readonly ITestOutputHelper _out;
    public DesktopFrameworkScanTests(ITestOutputHelper output) => _out = output;

    // This assembly references neither WinForms nor WPF, so it is a valid "clean" subject.
    private static ScanResult ScanSelf() =>
        AssemblyScan.ScanDlls("Tests", [typeof(DesktopFrameworkScanTests).Assembly.Location]);

    [Fact]
    public void A_mod_without_desktop_references_is_not_flagged()
    {
        var r = ScanSelf();
        Assert.Empty(r.DesktopAssemblies ?? []);
    }

    [Theory]
    [InlineData("System.Drawing.Common")]
    [InlineData("Microsoft.Win32.SystemEvents")]
    public void The_assemblies_the_launcher_supplies_are_not_blockers(string name) =>
        Assert.True(AssemblyScan.IsSuppliedOnServer(name));

    [Theory]
    [InlineData("System.Windows.Forms")]
    [InlineData("PresentationFramework")]
    [InlineData("WindowsBase")]
    [InlineData("System.Drawing.Design")]
    public void The_dialog_frameworks_are_never_supplied(string name) =>
        Assert.False(AssemblyScan.IsSuppliedOnServer(name));

    [Fact]
    public void System_Drawing_itself_is_not_treated_as_missing()
    {
        // System.Drawing and System.Drawing.Primitives are already in the server's Microsoft.NETCore.App, so
        // Color/Point/Rectangle arithmetic has always worked headless. Flagging them would be a false positive on a
        // large number of harmless mods, which is the fastest way to train the warning away.
        Assert.False(AssemblyScan.IsSuppliedOnServer("System.Drawing"));
        var r = AssemblyScan.ScanDlls("Fake", []);
        Assert.Empty(r.DesktopAssemblies ?? []);
    }

    [Fact]
    public void DismembermentPlus_is_flagged_when_it_is_installed()
    {
        var mods = InstalledMods.Find();
        if (mods is null || !mods.TryGetValue("DismembermentPlus", out var mod)) { _out.WriteLine("not installed; skipped"); return; }

        var r = AssemblyScan.Scan(mod);
        _out.WriteLine($"verdict {r.Verdict}; desktop: {string.Join(", ", r.DesktopAssemblies ?? [])}");

        Assert.Contains("System.Windows.Forms", r.DesktopAssemblies ?? []);
        Assert.Equal(ServerVerdict.NeedsReview, r.Verdict);
        Assert.Contains(r.Notes, n => n.Contains("does not ship"));
        // The blocker list leads with the unconditional failure, not the view assemblies.
        Assert.Equal("System.Windows.Forms", r.Blockers.First());
    }
}
