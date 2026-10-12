using Mono.Cecil;
using ModderLords.CompatSync.Coop;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// BellumClientKingdoms reaches Bellum's kingdom maker and the campaign's kingdom lists by name. If either is renamed
/// the patch only logs a warning in game and a player's Kingdom screen can open black again, so the installed game
/// and Bellum are checked here instead. Both checks pass with nothing to check on a machine without them (CI).
/// </summary>
public class BellumClientKingdomsShapeTests
{
    private static ModuleDefinition? Installed(params string[] relative)
    {
        var root = GamePaths.FindGameRoot();
        var dll = root is null ? null : Path.Combine(new[] { root }.Concat(relative).ToArray());
        return dll is null || !File.Exists(dll) ? null : ModuleDefinition.ReadModule(dll);
    }

    [Fact]
    public void Installed_Bellum_still_founds_every_kingdom_through_one_maker_that_may_refuse()
    {
        using var module = Installed("Modules", "BellumCivile", "bin", "Win64_Shipping_Client", "BellumCivile.dll");
        if (module is null) return;

        var maker = module.GetType(BellumClientKingdomTargets.Maker);
        Assert.NotNull(maker);
        var create = Assert.Single(maker!.Methods, m => m.Name == BellumClientKingdomTargets.MakerMethod);
        Assert.True(create.IsStatic);
        Assert.Equal("TaleWorlds.CampaignSystem.Kingdom", create.ReturnType.FullName);
        Assert.Equal("System.String", create.Parameters[0].ParameterType.FullName);
        // It already returns null for a founder who cannot rule, which is why returning null on a player's game is safe.
        Assert.Contains(create.Body.Instructions, i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldnull);

        // Nothing else in Bellum calls the game's own Kingdom.CreateKingdom, so the one patch covers every path.
        var direct = module.Types.SelectMany(t => t.NestedTypes.Prepend(t)).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Where(m => m.Body.Instructions.Any(i => i.Operand is MethodReference { Name: "CreateKingdom", DeclaringType.FullName: "TaleWorlds.CampaignSystem.Kingdom" }))
            .Select(m => m.DeclaringType.FullName).Distinct().ToList();
        Assert.Equal(new[] { BellumClientKingdomTargets.Maker }, direct);
    }

    [Fact]
    public void The_installed_game_still_keeps_kingdoms_in_the_two_lists_the_sweep_removes_from()
    {
        using var module = Installed("bin", "Win64_Shipping_Client", "TaleWorlds.CampaignSystem.dll");
        if (module is null) return;

        var objects = module.GetType("TaleWorlds.CampaignSystem.CampaignObjectManager");
        Assert.NotNull(objects);
        Assert.Contains(objects!.Fields, f => f.Name == "_kingdoms");
        Assert.Contains(objects.Fields, f => f.Name == "_factions");
        Assert.Contains(objects.Properties, p => p.Name == "Kingdoms");
    }
}
