using System.Text.RegularExpressions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Coop's Player.HeroId is an object-manager id ("Hero_Player"), not the hero's StringId ("Player"). Comparing the two,
/// or looking a HeroId up in MBObjectManager, silently never matches: Bellum actions were rejected as "clan ownership
/// changed" and TAOM/Living Economy notices never reached players. The adapter cannot be unit-tested without the game,
/// so this pins the rule at the source: go through PlayerHeroes (Coop's object manager) instead.
/// </summary>
public class PlayerHeroIdTests
{
    private static readonly string CoopAdapterDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ModderLords.CompatSync.Coop"));

    [Fact]
    public void No_player_hero_id_is_compared_with_a_string_id_or_looked_up_in_MBObjectManager()
    {
        var mixing = new Regex(@"HeroId\s*==\s*\w+\.StringId|StringId\s*==\s*\w+\.HeroId|MBObjectManager\.Instance\.GetObject<Hero>\(\s*\w*\.?HeroId");
        var offenders = Directory.GetFiles(CoopAdapterDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => mixing.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Player.HeroId mixed with StringId:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_scan_still_sees_the_adapter_sources()
    {
        Assert.True(File.Exists(Path.Combine(CoopAdapterDir, "PlayerHeroes.cs")), CoopAdapterDir);
    }
}
