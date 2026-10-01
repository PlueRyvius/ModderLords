using Mono.Cecil;
using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Tests;

/// <summary>
/// KingdomVoteTimeoutAi reaches into Coop's private vote manager by name. If Coop renames any of it the patch only logs
/// a warning in game and players go back to abstaining on a timeout, so the installed Coop is checked here instead.
/// </summary>
public class KingdomVoteTimeoutShapeTests
{
    [Fact]
    public void Installed_Coop_still_has_the_vote_deadline_shape_the_timeout_patch_reads()
    {
        var gameInterface = CoopSinks.FindGameInterface(GamePaths.SteamLibraries());
        if (gameInterface is null) return; // no Coop install on this machine (CI)

        using var module = ModuleDefinition.ReadModule(gameInterface);
        var manager = module.GetType("GameInterface.Services.Kingdoms.KingdomDecisionVoteManager");
        Assert.NotNull(manager);
        var deadline = Assert.Single(manager!.Methods, m => m.Name == "ApplyMissingAbstentions");
        var parameter = Assert.Single(deadline.Parameters);
        Assert.Equal("state", parameter.Name); // the Harmony postfix binds it by this name
        Assert.Contains(manager.Methods, m => m.Name == "TryGetClan" && m.Parameters.Count == 3);

        var state = parameter.ParameterType.Resolve();
        foreach (var property in new[] { "EligibleClanIds", "FinalVotes", "Decision", "Election" })
            Assert.Contains(state.Properties, p => p.Name == property);
    }
}
