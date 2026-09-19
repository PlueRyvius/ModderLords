using Bannerlord.ModuleManager;
using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;

namespace ModderLords.Core.Tests;

/// <summary>
/// Run must not hand the engine a SubModuleClassType the mod's DLLs do not define.
///
/// Regression cover for FamilyAppearanceEditor (2026-09-19). It ships a Client submodule and a Dedicated Server
/// submodule, so honouring the split correctly removes the client half - and then the server loads
/// FamilyAppearanceEditor.DedicatedServerSubModule, a class in neither shipped DLL, and dies with a native access
/// violation instead. Honouring the split is necessary but not sufficient; the class has to be there.
/// </summary>
public class ServerSubmoduleClassTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ServerSubmoduleClassTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string ClientClass = "Mod.SubModule";
    private const string ServerClass = "Mod.DedicatedServerSubModule";

    private static SubModuleInfoExtended Sub(string name, string classType, string? dedicatedServerType)
    {
        var tags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (dedicatedServerType is not null) tags["DedicatedServerType"] = [dedicatedServerType];
        if (dedicatedServerType == "none") tags["IsNoRenderModeElement"] = ["false"];
        return new SubModuleInfoExtended { Name = name, DLLName = "Mod.dll", SubModuleClassType = classType, Tags = tags };
    }

    /// <summary>A module that split itself: one client submodule, one dedicated-server submodule.</summary>
    private static DiscoveredModule SplitMod(string id = "Mod") =>
        new(id, "v1.0.0", @"X\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id,
            Name = id,
            SubModules = [Sub("Client", ClientClass, "none"), Sub("Dedicated Server", ServerClass, "custom")],
        });

    /// <summary>The ordinary case: one submodule, tagged client-only, no server variant on offer.</summary>
    private static DiscoveredModule SingleSubmoduleMod(string id = "Plain") =>
        new(id, "v1.0.0", @"X\" + id, ModuleSourceKind.Workshop, new ModuleInfoExtended
        {
            Id = id,
            Name = id,
            SubModules = [Sub("Plain", "Plain.SubModule", "none")],
        });

    private static OverlayPlan PlanWith(DiscoveredModule mod, ServerRole role, params string[] definedTypes) =>
        OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(mod, role)],
            scan: _ => new ScanResult(mod.Id, ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false),
            record: _ => null,
            typeNames: _ => new HashSet<string>(definedTypes, StringComparer.Ordinal));

    [Fact]
    public void Run_survives_when_the_server_class_is_present()
    {
        var entry = Assert.Single(PlanWith(SplitMod(), ServerRole.Run, ClientClass, ServerClass).Entries);

        Assert.Equal(ServerRole.Run, entry.Selection.Role);
        Assert.DoesNotContain(entry.Notes, n => n.Contains("not in this mod's DLLs"));
    }

    [Fact]
    public void Run_falls_back_to_DependencyOnly_when_the_server_class_is_missing()
    {
        // The FamilyAppearanceEditor shape: the manifest promises a server class the DLLs never defined.
        var entry = Assert.Single(PlanWith(SplitMod(), ServerRole.Run, ClientClass).Entries);

        Assert.Equal(ServerRole.DependencyOnly, entry.Selection.Role);
        Assert.Contains(entry.Notes, n => n.Contains(ServerClass) && n.Contains("not in this mod's DLLs"));
        Assert.Contains(entry.Notes, n => n.Contains("DependencyOnly"));
    }

    [Fact]
    public void The_fallback_reaches_the_overlay_shape_not_just_the_note()
    {
        // The reorder guard. The role is resolved before manifestNeedsRewrite and kind are computed; if a downgrade
        // were applied after them, the note would look right while the overlay was still built for Run. A
        // DependencyOnly module with code always needs a manifest rewrite, so it must be a Shadow.
        var entry = Assert.Single(PlanWith(SplitMod(), ServerRole.Run, ClientClass).Entries);

        Assert.Equal(OverlayKind.Shadow, entry.Kind);
        Assert.NotNull(entry.ShadowPath);
        Assert.Contains(entry.Notes, n => n.Contains("dependency-only: kept in the module list for the handshake"));
    }

    [Fact]
    public void A_mod_with_no_server_variant_is_never_downgraded()
    {
        // MyLittleWarband's shape, and the case most at risk from this change: client-only tags, no server submodule
        // to fall back to, and it has been running fine on the server all along. Run must still mean Run, and the
        // DLLs must not even be consulted.
        var consulted = false;
        var mod = SingleSubmoduleMod();
        var plan = OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(mod, ServerRole.Run)],
            scan: _ => new ScanResult(mod.Id, ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false),
            record: _ => null,
            typeNames: _ => { consulted = true; return []; });

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(ServerRole.Run, entry.Selection.Role);
        Assert.Contains(entry.Notes, n => n.Contains("stripping them so the server loads it"));
        Assert.False(consulted, "a mod without a server variant has nothing to verify");
    }

    [Fact]
    public void Unreadable_DLLs_are_a_guard_not_a_gate()
    {
        // Proving absence needs something to prove it against. An empty or throwing type index must let the launch
        // proceed rather than downgrading every split mod on a machine where the DLLs cannot be read.
        var mod = SplitMod();
        foreach (var typeNames in new Func<DiscoveredModule, HashSet<string>>[]
                 { _ => [], _ => throw new IOException("locked") })
        {
            var entry = Assert.Single(OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(mod, ServerRole.Run)],
                scan: _ => new ScanResult(mod.Id, ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false),
                record: _ => null, typeNames: typeNames).Entries);

            Assert.Equal(ServerRole.Run, entry.Selection.Role);
        }
    }

    [Fact]
    public void Roles_other_than_Run_are_untouched()
    {
        foreach (var role in new[] { ServerRole.DependencyOnly, ServerRole.AsShipped })
        {
            var entry = Assert.Single(PlanWith(SplitMod(), role, ClientClass).Entries);
            Assert.Equal(role, entry.Selection.Role);
        }
    }

    // ---- desktop frameworks ----------------------------------------------------------------------------------

    private static ScanResult ScanWithDesktop(string id, params string[] desktop) =>
        new(id, desktop.Length > 0 ? ServerVerdict.NeedsReview : ServerVerdict.ServerSafe,
            [], [], [], [], [], [], [], false, null, desktop);

    private static OverlayPlan PlanWithScan(DiscoveredModule mod, ScanResult result, Func<string, CompatRecord?>? record = null) =>
        OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(mod, ServerRole.Run)],
            scan: _ => result, record: record ?? (_ => null), typeNames: _ => [ClientClass, ServerClass, "Plain.SubModule"]);

    [Fact]
    public void A_desktop_framework_reference_falls_back_to_DependencyOnly()
    {
        // Warning alone was tried and did not work: on 2026-09-19 DismembermentPlus was warned about on line 3 of a
        // 2,687-line log and killed the server anyway, 2,684 lines later. The assembly is absent, so this is a
        // certainty rather than a risk - the same answer the missing-class check gives.
        var mod = SingleSubmoduleMod();
        var entry = Assert.Single(PlanWithScan(mod, ScanWithDesktop(mod.Id, "System.Windows.Forms")).Entries);

        Assert.Equal(ServerRole.DependencyOnly, entry.Selection.Role);
        Assert.Contains(entry.Notes, n => n.Contains("System.Windows.Forms") && n.Contains("does not ship"));
        Assert.Contains(entry.Notes, n => n.Contains("DependencyOnly"));
    }

    [Fact]
    public void A_desktop_reference_is_caught_even_when_the_mod_never_tagged_itself()
    {
        // The whole reason this check is not gated on HasHeadlessExclusions. A mod that forgot the tag dies exactly
        // the same way, and is the case nobody is watching for.
        var untagged = new DiscoveredModule("Untagged", "v1.0.0", @"X\Untagged", ModuleSourceKind.Workshop,
            new ModuleInfoExtended
            {
                Id = "Untagged", Name = "Untagged",
                SubModules = [Sub("Untagged", "Untagged.SubModule", dedicatedServerType: null)],
            });
        Assert.False(untagged.HasHeadlessExclusions, "fixture must be untagged for this test to mean anything");

        var entry = Assert.Single(PlanWithScan(untagged, ScanWithDesktop("Untagged", "PresentationFramework")).Entries);

        Assert.Equal(ServerRole.DependencyOnly, entry.Selection.Role);
    }

    [Fact]
    public void A_curated_record_that_says_Run_outranks_the_desktop_scan()
    {
        // A tested result beats a static guess, and the scan is skipped entirely in that case.
        var mod = SingleSubmoduleMod();
        var vouched = new CompatRecord { Id = mod.Id, DefaultRole = ServerRole.Run, Verdict = CompatVerdict.Works };

        var entry = Assert.Single(PlanWithScan(mod, ScanWithDesktop(mod.Id, "System.Windows.Forms"), _ => vouched).Entries);

        Assert.Equal(ServerRole.Run, entry.Selection.Role);
    }

    [Fact]
    public void No_desktop_reference_leaves_Run_alone()
    {
        var mod = SingleSubmoduleMod();
        var entry = Assert.Single(PlanWithScan(mod, ScanWithDesktop(mod.Id)).Entries);

        Assert.Equal(ServerRole.Run, entry.Selection.Role);
        Assert.DoesNotContain(entry.Notes, n => n.Contains("does not ship"));
    }

    [Fact]
    public void The_dotnet_crash_code_does_not_promise_exception_text_that_is_not_there()
    {
        // It used to say "The exception text is in the log above". For this crash class there is none - the handler
        // that would have logged it is part of what failed - so that sent people hunting for nothing.
        var text = ModderLords.Coop.Launch.ExitCodeExplainer.Explain(unchecked((int)0xE0434352));

        Assert.DoesNotContain("is in the log above", text);
        Assert.Contains("WARNING", text);
    }

    // ---- AssemblyScan.TypeNames ------------------------------------------------------------------------------

    [Fact]
    public void TypeNames_reads_the_types_defined_in_a_real_assembly()
    {
        var names = AssemblyScan.TypeNames([typeof(ServerSubmoduleClassTests).Assembly.Location]);

        Assert.Contains("ModderLords.Core.Tests.ServerSubmoduleClassTests", names);
        Assert.DoesNotContain("ModderLords.Core.Tests.NoSuchTypeAnywhere", names);
    }

    [Fact]
    public void TypeNames_survives_a_file_that_is_not_an_assembly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(tmp, "definitely not a PE file");
        try { Assert.Empty(AssemblyScan.TypeNames([tmp, Path.Combine(Path.GetTempPath(), "does-not-exist.dll")])); }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// The fixtures prove the rule; this proves it against the manifests and DLLs that caused the crash. Runs with
    /// the REAL type index, no injected delegate. Reports which mods it actually reached, so a machine where neither
    /// is installed cannot look like a pass.
    /// </summary>
    [Fact]
    public void The_installed_split_mods_resolve_as_expected()
    {
        var mods = InstalledMods.Find();
        if (mods is null) { _out.WriteLine("no game; skipped"); return; }

        var checkedAny = false;
        foreach (var (id, expected) in new[]
                 {
                     // Ships a real Win64_Shipping_Server bin whose DLL genuinely defines the class.
                     ("CoopMarriage", ServerRole.Run),
                     // Declares FamilyAppearanceEditor.DedicatedServerSubModule; no server bin, class in no DLL.
                     ("FamilyAppearanceEditor", ServerRole.DependencyOnly),
                 })
        {
            if (!mods.TryGetValue(id, out var mod)) { _out.WriteLine($"{id}: not installed"); continue; }
            if (!mod.HasHeadlessExclusions) { _out.WriteLine($"{id}: no client-only tags; nothing to resolve"); continue; }

            var entry = Assert.Single(OverlayPlanner.Plan(@"X\overlay", @"X\engine", [new ModSelection(mod, ServerRole.Run)],
                record: _ => null).Entries);
            _out.WriteLine($"{id}: {entry.Selection.Role}");
            foreach (var n in entry.Notes.Where(n => n.Contains("WARNING"))) _out.WriteLine("   " + n);

            Assert.Equal(expected, entry.Selection.Role);
            checkedAny = true;
        }

        if (!checkedAny) _out.WriteLine("neither mod installed; nothing asserted");
    }
}
