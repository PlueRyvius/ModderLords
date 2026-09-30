using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModderLords.Core.Compat;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Where a desktop-framework reference sits decides whether it kills the server: a missing assembly only fails the
/// method that names it, when that method is compiled. The fixtures compile a stand-in System.Windows.Forms and a
/// stand-in MBSubModuleBase, then mods that use the first in each position that matters.
/// </summary>
public sealed class DesktopSiteScanTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-desktop-sites-" + Guid.NewGuid().ToString("N"));
    private readonly MetadataReference _forms;
    private readonly MetadataReference _engine;

    public DesktopSiteScanTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
        _forms = Compile("System.Windows.Forms", """
            namespace System.Windows.Forms
            {
                public static class MessageBox { public static void Show(string text) { } }
                public class SaveFileDialog { public string FileName = ""; public void ShowDialog() { } }
                public class Form { }
            }
            """);
        _engine = Compile("TaleWorlds.MountAndBlade", """
            namespace TaleWorlds.MountAndBlade
            {
                public abstract class MBSubModuleBase
                {
                    protected virtual void OnSubModuleLoad() { }
                    protected virtual void OnApplicationTick(float dt) { }
                }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MetadataReference Compile(string name, string source, params MetadataReference[] refs)
    {
        var path = Path.Combine(_dir, name + ".dll");
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
             MetadataReference.CreateFromFile(typeof(Thread).Assembly.Location), .. refs],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromFile(path);
    }

    private DesktopSites Sites(string source)
    {
        var mod = Compile("Mod" + Guid.NewGuid().ToString("N")[..8], source, _forms, _engine);
        var sites = DesktopSiteScan.Analyze([((PortableExecutableReference)mod).FilePath!], n => n == "System.Windows.Forms");
        _out.WriteLine("load: " + string.Join(" | ", sites.LoadSites));
        _out.WriteLine("deferred: " + string.Join(" | ", sites.DeferredSites));
        return sites;
    }

    [Fact]
    public void A_call_in_a_catch_block_of_OnSubModuleLoad_is_on_the_load_path()
    {
        // DismembermentPlus, 2026-09-19: the catch never ran, and the server died anyway.
        var s = Sites("""
            public class Sub : TaleWorlds.MountAndBlade.MBSubModuleBase
            {
                protected override void OnSubModuleLoad()
                {
                    try { Go(); } catch { System.Windows.Forms.MessageBox.Show("boom"); }
                }
                private static void Go() { }
            }
            """);
        Assert.Contains(s.LoadSites, x => x.StartsWith("Sub::OnSubModuleLoad"));
    }

    [Fact]
    public void A_helper_the_submodule_calls_directly_is_on_the_load_path()
    {
        var s = Sites("""
            public static class Report { public static void Fail(string m) => System.Windows.Forms.MessageBox.Show(m); }
            public class Sub : TaleWorlds.MountAndBlade.MBSubModuleBase
            {
                protected override void OnSubModuleLoad() { try { } catch { Report.Fail("x"); } }
            }
            """);
        Assert.Contains(s.LoadSites, x => x.StartsWith("Report::Fail") && x.Contains("Sub::OnSubModuleLoad"));
        Assert.Empty(s.DeferredSites);
    }

    [Fact]
    public void A_dialog_on_its_own_thread_behind_a_hotkey_is_deferred()
    {
        // RBM 4.5.2's shape: OnApplicationTick -> TickInput -> SavePresetToFile -> ShowSaveDialog, which starts an STA
        // thread whose lambda holds every WinForms call. Nothing on the tick path names WinForms itself.
        var s = Sites("""
            public static class CustomBattlePatches
            {
                public static object? BattleVM;
                public static void TickInput() { if (BattleVM == null) return; SavePresetToFile(); }
                public static void SavePresetToFile() { var p = ShowSaveDialog(); }
                public static string? ShowSaveDialog()
                {
                    string? result = null;
                    var t = new System.Threading.Thread(() => { var d = new System.Windows.Forms.SaveFileDialog(); d.ShowDialog(); result = d.FileName; });
                    t.Start(); t.Join();
                    return result;
                }
            }
            public class Sub : TaleWorlds.MountAndBlade.MBSubModuleBase
            {
                protected override void OnApplicationTick(float dt) => CustomBattlePatches.TickInput();
            }
            """);
        Assert.Empty(s.LoadSites);
        Assert.Contains(s.DeferredSites, x => x.Contains("<ShowSaveDialog>"));
    }

    [Theory]
    [InlineData("public class Holder { public System.Windows.Forms.Form? F; }", "Holder.F (field)")]
    [InlineData("public class Holder { public void Take(System.Windows.Forms.Form f) { } }", "Holder::Take (signature)")]
    [InlineData("public class MyForm : System.Windows.Forms.Form { }", "MyForm (base type)")]
    [InlineData("public class Holder { static Holder() { System.Windows.Forms.MessageBox.Show(\"x\"); } }", "Holder::.cctor (static constructor)")]
    public void Type_shape_and_static_constructors_are_on_the_load_path(string source, string expected)
    {
        var s = Sites(source);
        Assert.Contains(expected, s.LoadSites);
    }

    [Fact]
    public void A_mod_without_the_reference_has_no_sites()
    {
        var s = Sites("public class Plain { public object Go() => new object(); }");
        Assert.Empty(s.LoadSites);
        Assert.Empty(s.DeferredSites);
    }

    // ---- the real mods, when installed ----------------------------------------------------------------------

    [Fact]
    public void Installed_RBM_keeps_its_desktop_references_off_the_load_path()
    {
        var mods = InstalledMods.Find();
        if (mods is null || !mods.TryGetValue("RBM", out var mod)) { _out.WriteLine("not installed; skipped"); return; }

        var r = AssemblyScan.Scan(mod);
        _out.WriteLine($"desktop: {string.Join(", ", r.DesktopAssemblies ?? [])}");
        foreach (var n in r.Notes) _out.WriteLine("  " + n);
        if ((r.DesktopAssemblies ?? []).Count == 0) { _out.WriteLine("this RBM has no desktop reference; nothing to assert"); return; }

        Assert.NotNull(r.DesktopSites);
        Assert.Empty(r.DesktopSites!.LoadSites);
        Assert.NotEmpty(r.DesktopSites.DeferredSites);
    }

    [Fact]
    public void Installed_DismembermentPlus_is_still_on_the_load_path()
    {
        var mods = InstalledMods.Find();
        if (mods is null || !mods.TryGetValue("DismembermentPlus", out var mod)) { _out.WriteLine("not installed; skipped"); return; }

        var r = AssemblyScan.Scan(mod);
        foreach (var n in r.Notes) _out.WriteLine("  " + n);

        Assert.NotNull(r.DesktopSites);
        Assert.NotEmpty(r.DesktopSites!.LoadSites);
    }
}
