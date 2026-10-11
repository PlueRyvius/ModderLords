using Mono.Cecil;
using ModderLords.CompatSync.Coop;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// BellumVotePeace raises Bellum Civile's private treaty mark by name. If Bellum renames it the patch only logs a
/// warning in game and a voted peace is blocked again, so the installed Bellum is checked here instead.
/// </summary>
public class BellumVotePeaceShapeTests
{
    [Fact]
    public void Installed_Bellum_still_has_the_treaty_mark_and_its_peace_guard_still_reads_it()
    {
        var root = GamePaths.FindGameRoot();
        var dll = root is null ? null : Path.Combine(GamePaths.ModulesDir(root), "BellumCivile", "bin", "Win64_Shipping_Client", "BellumCivile.dll");
        if (dll is null || !File.Exists(dll)) return; // no Bellum install on this machine (CI)

        using var module = ModuleDefinition.ReadModule(dll);
        var context = module.GetType(BellumPeaceTargets.TreatyMark);
        Assert.NotNull(context);
        var depth = Assert.Single(context!.Fields, f => f.Name == BellumPeaceTargets.TreatyMarkDepth);
        Assert.True(depth.IsStatic);
        Assert.Equal("System.Int32", depth.FieldType.FullName);

        // The guard that blocks peace Bellum did not make must still let a marked peace through.
        var guard = module.GetType(BellumPeaceTargets.PeaceGuard);
        Assert.NotNull(guard);
        Assert.Contains(guard!.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions),
            i => i.Operand is MethodReference { Name: "get_IsBellumPeaceResolution" });
    }
}
