using ModderLords.CompatSync;
using ModderLords.Core.Compat;
using ModderLords.Core.Compat.Authority;
using ModderLords.Coop.Compat;

namespace ModderLords.Core.Tests;

public sealed class LegacyOperationBoundaryTests
{
    [Theory]
    [InlineData("Handlers")]
    [InlineData("Unpatch")]
    [InlineData("PlayerComparisons")]
    [InlineData("Relays")]
    [InlineData("SyncState")]
    public void StaleGeneratedActionsCannotEnterLegacyRuntimeEvenWhenSchemaIsSpoofed(string field)
    {
        var json = "{\"SchemaVersion\":1,\"Mods\":[{\"CampaignBehaviors\":[\"Legacy\"]},{\"" + field + "\":[\"Unsafe\"]}]}";
        Assert.False(LegacyRecipePolicy.Accept(json, out var reason));
        Assert.Contains("validated operation", reason);
    }
    [Fact] public void AuthoritySuggestionsCannotChangeLegacySelections()
    {
        var scan = new ScanResult("Mod", ServerVerdict.ServerSafe, [], [], [], [], ["Mod.Tick", "Mod.Ui"], [], [], false);
        var report = new AuthorityReport { ModuleId = "Mod", PlayerComparisonMethods = ["Mod.Tick::Award"], RelayMethods = ["Mod.Tick::Buy"] };
        var recipe = RecipeSet.Build([("Mod", scan, (IReadOnlyCollection<string>)new[] { "Mod.Ui" })], "test",
            authority: new Dictionary<string, AuthorityReport> { ["Mod"] = report });
        Assert.Equal(1, recipe.SchemaVersion);
        var mod = Assert.Single(recipe.Mods);
        Assert.Equal(["Mod.Tick"], mod.CampaignBehaviors);
        Assert.Empty(mod.PlayerComparisons); Assert.Empty(mod.Relays); Assert.Empty(mod.Handlers);
        Assert.True(LegacyRecipePolicy.Accept(recipe.ToJson(), out _));
    }
    [Theory]
    [InlineData("{\"SchemaVersion\":3,\"Mods\":[]}")]
    [InlineData("{\"Mods\":false}")]
    [InlineData("invalid")]
    public void UnsupportedOrMalformedLegacyInputsAreRefused(string json) => Assert.False(LegacyRecipePolicy.Accept(json, out _));
}
