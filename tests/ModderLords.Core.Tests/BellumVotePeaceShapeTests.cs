using Mono.Cecil;
using ModderLords.CompatSync.Coop;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// BellumVotePeace and BellumTreatyVote reach into Bellum Civile by name: its private treaty mark, its ratification
/// methods and their parameter names, and its treaty records. If Bellum renames any of it the patches only log a
/// warning in game and peace for a players' realm stops working, so the installed Bellum is checked here instead.
/// </summary>
public class BellumVotePeaceShapeTests
{
    private static ModuleDefinition? InstalledBellum()
    {
        var root = GamePaths.FindGameRoot();
        var dll = root is null ? null : Path.Combine(GamePaths.ModulesDir(root), "BellumCivile", "bin", "Win64_Shipping_Client", "BellumCivile.dll");
        return dll is null || !File.Exists(dll) ? null : ModuleDefinition.ReadModule(dll);   // no Bellum install on this machine (CI)
    }

    [Fact]
    public void Installed_Bellum_still_has_the_treaty_mark_and_its_peace_guard_still_reads_it()
    {
        using var module = InstalledBellum();
        if (module is null) return;

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

    [Fact]
    public void Installed_Bellum_still_ratifies_treaties_through_the_methods_the_treaty_vote_patches()
    {
        using var module = InstalledBellum();
        if (module is null) return;

        var treaty = module.GetType(BellumPeaceTargets.Treaty);
        Assert.NotNull(treaty);
        HasParameters(treaty!, BellumPeaceTargets.Resolve, BellumPeaceTargets.ResolveParameters);
        HasParameters(treaty!, BellumPeaceTargets.Ratify, BellumPeaceTargets.RatifyParameters);
        HasParameters(treaty!, BellumPeaceTargets.Apply, BellumPeaceTargets.ApplyParameters);

        // Ratification is still decided inside ResolvePendingParley by EvaluateRatification, then applied or rejected.
        var resolve = treaty!.Methods.Single(m => m.Name == BellumPeaceTargets.Resolve);
        var calls = resolve.Body.Instructions.Select(i => (i.Operand as MethodReference)?.Name).ToList();
        Assert.Contains(BellumPeaceTargets.Ratify, calls);
        Assert.Contains(BellumPeaceTargets.Apply, calls);

        var accepts = treaty.Methods.Single(m => m.Name == BellumPeaceTargets.Ratify).Parameters;
        foreach (var name in new[] { "winnerAccepts", "loserAccepts", "winnerUsesOverride", "loserUsesOverride" })
            Assert.Equal("System.Boolean&", accepts.Single(p => p.Name == name).ParameterType.FullName);

        var summary = Assert.Single(treaty.Methods, m => m.Name == BellumPeaceTargets.Summary);
        Assert.True(summary.IsStatic);
        Assert.Equal(2, summary.Parameters.Count);
    }

    [Fact]
    public void Installed_Bellum_still_has_the_treaty_records_and_the_minimum_war_duration_check()
    {
        using var module = InstalledBellum();
        if (module is null) return;

        var proposal = module.GetType(BellumPeaceTargets.Proposal);
        Assert.NotNull(proposal);
        Assert.All(BellumPeaceTargets.ProposalMembers, name => Assert.Contains(proposal!.Properties, p => p.Name == name));
        var state = proposal!.Properties.Single(p => p.Name == "State").PropertyType.Resolve();
        Assert.Contains(state.Fields, f => f.Name == BellumPeaceTargets.PendingState);
        Assert.Contains(state.Fields, f => f.Name == "Applied");

        var term = module.GetType(BellumPeaceTargets.Term);
        Assert.NotNull(term);
        Assert.All(BellumPeaceTargets.TermMembers, name => Assert.Contains(term!.Properties, p => p.Name == name));
        var kind = term!.Properties.Single(p => p.Name == "Type").PropertyType.Resolve();
        Assert.Contains(kind.Fields, f => f.Name == BellumPeaceTargets.TributeTerm);
        Assert.Contains(kind.Fields, f => f.Name == BellumPeaceTargets.WhitePeaceTerm);

        var minimum = module.GetType(BellumPeaceTargets.MinimumDuration);
        Assert.NotNull(minimum);
        var check = Assert.Single(minimum!.Methods, m => m.Name == BellumPeaceTargets.MinimumDurationCheck);
        Assert.True(check.IsStatic);
        Assert.Equal(5, check.Parameters.Count);
        Assert.Equal("System.Boolean", check.ReturnType.FullName);
    }

    private static void HasParameters(TypeDefinition type, string method, IReadOnlyList<string> parameters)
    {
        var found = Assert.Single(type.Methods, m => m.Name == method);
        Assert.All(parameters, name => Assert.Contains(found.Parameters, p => p.Name == name));
    }

    [Fact]
    public void A_realm_pays_what_it_owes_less_what_it_is_owed()
    {
        var terms = new[]
        {
            new BellumPeaceTargets.Tribute("loser", "winner", 300, 60),
            new BellumPeaceTargets.Tribute("winner", "loser", 50, 20),
            new BellumPeaceTargets.Tribute("third", "winner", 999, 999),   // another realm's term in the same treaty
        };
        Assert.Equal((250, 60), BellumPeaceTargets.TributePaidBy("loser", terms.Take(2)));
        Assert.Equal((-250, 60), BellumPeaceTargets.TributePaidBy("winner", terms.Take(2)));
        Assert.Equal((250, 60), BellumPeaceTargets.TributePaidBy("loser", terms));
        Assert.Equal((-1249, 999), BellumPeaceTargets.TributePaidBy("winner", terms));
    }

    [Fact]
    public void No_tribute_term_means_no_tribute_on_the_vote()
    {
        Assert.Equal((0, 0), BellumPeaceTargets.TributePaidBy("a", Array.Empty<BellumPeaceTargets.Tribute>()));
        Assert.Equal((0, 0), BellumPeaceTargets.TributePaidBy("a", new[] { new BellumPeaceTargets.Tribute("a", "b", 0, 30), new BellumPeaceTargets.Tribute("a", "b", 100, 0) }));
        // Equal and opposite terms cancel: nothing is paid, so no duration either.
        Assert.Equal((0, 0), BellumPeaceTargets.TributePaidBy("a", new[] { new BellumPeaceTargets.Tribute("a", "b", 100, 30), new BellumPeaceTargets.Tribute("b", "a", 100, 30) }));
    }
}
