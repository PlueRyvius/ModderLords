using Bannerlord.ModuleManager;
using ModularCoop.Core.Modules;
using Xunit;
using Xunit.Abstractions;

namespace ModularCoop.Core.Tests;

/// <summary>Diagnostic: validates the bundled ModularCoop.Compat manifest the way the BUTR launcher does, against the local game install.</summary>
public class ManifestValidationProbe
{
    private readonly ITestOutputHelper _out;
    public ManifestValidationProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Compat_manifest_validates_against_local_game_modules()
    {
        var game = ModuleCatalog.FindGameRoot(Launch.ServerPaths.SteamLibraries());
        if (game is null) { _out.WriteLine("no game install; skipped"); return; }
        var mods = new List<ModuleInfoExtended>();
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(game, "Modules")))
        {
            var m = ModuleCatalog.TryParse(dir, ModuleSourceKind.GameModules, out _);
            if (m is not null) mods.Add(m.Info);
        }
        var target = mods.FirstOrDefault(m => m.Id == "ModularCoop.Compat");
        if (target is null) { _out.WriteLine("ModularCoop.Compat not installed in game Modules; skipped"); return; }
        _out.WriteLine("DependentModules: " + string.Join(", ", target.DependentModules.Select(d => d.Id + (d.IsOptional ? "?" : ""))));
        _out.WriteLine("Metadatas: " + string.Join(", ", target.DependentModuleMetadatas.Select(d => d.ToString())));
        var selected = new HashSet<string>(new[] { "Bannerlord.Harmony", "Bannerlord.UIExtenderEx", "Bannerlord.ButterLib", "Bannerlord.MBOptionScreen", "Native", "SandBoxCore", "CustomBattle", "Sandbox", "StoryMode", "ModularCoop.Compat" });
        var issues = ModuleUtilities.ValidateModule(mods, target, m => selected.Contains(m.Id)).ToList();
        foreach (var i in issues) _out.WriteLine("ISSUE: " + i);
        var order = ModuleUtilities.ValidateLoadOrder(ModuleSorter.Sort(mods.Where(m => selected.Contains(m.Id)).ToList()).ToList(), target).ToList();
        foreach (var i in order) _out.WriteLine("ORDER: " + i);
        _out.WriteLine(issues.Count == 0 && order.Count == 0 ? "VALID" : "INVALID");
    }
}
