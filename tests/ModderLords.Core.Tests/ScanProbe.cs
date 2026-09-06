using ModderLords.Core.Compat;
using ModderLords.Core.Modules;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>Diagnostic: prints the assembly scan for the local ImprovedGarrisons / HealOnKill installs when present.</summary>
public class ScanProbe
{
    private readonly ITestOutputHelper _out;
    public ScanProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Scan_local_mods()
    {
        var game = ModuleCatalog.FindGameRoot(Launch.ServerPaths.SteamLibraries());
        if (game is null) { _out.WriteLine("no game; skipped"); return; }
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(game, "Modules")))
        {
            var m = ModuleCatalog.TryParse(dir, ModuleSourceKind.GameModules, out _);
            if (m is null || m.Id is not ("ImprovedGarrisons" or "HealOnKill" or "ModularSmithing2")) continue;
            var s = AssemblyScan.Scan(m);
            _out.WriteLine($"{m.Id}: {s.Summary}; campaign=[{string.Join(", ", s.CampaignBehaviors)}]; mission=[{string.Join(", ", s.MissionBehaviors)}]; notes=[{string.Join("; ", s.Notes)}]");
            _out.WriteLine($"  settings: {s.SettingsSummary}; classes=[{string.Join(", ", s.SettingsClasses)}]");
        }
    }
}
