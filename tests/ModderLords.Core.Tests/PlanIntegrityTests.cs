using System.Text.Json;
using ModderLords.Analysis;
using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

public sealed class PlanIntegrityTests
{
    private static JObject Plan()
    {
        var request = new AnalysisRequest([], [], [], "v1.4.8");
        return PlanIntegrity.Parse(JsonSerializer.Serialize(CompatibilityPlanner.Build(request, [], []), AnalysisJson.Options));
    }
    [Fact] public void LauncherPlanVerifiesWithRuntimeEncoding()
    {
        var plan = Plan(); Assert.True(PlanIntegrity.Verify(plan, out _));
        var reordered = new JObject(plan.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value.DeepClone())));
        Assert.True(PlanIntegrity.Verify(reordered, out _));
        Assert.True(PlanIntegrity.Verify(PlanIntegrity.Parse(reordered.ToString(Newtonsoft.Json.Formatting.None)), out _));
    }
    [Theory] [InlineData("Contracts")] [InlineData("Fingerprints")] [InlineData("ContextDigest")]
    public void ChangingPlanContentCannotReuseDigest(string field)
    {
        var plan = Plan();
        plan[field] = field == "ContextDigest" ? JValue.CreateString(new string('A', 64)) : new JArray("changed");
        Assert.False(PlanIntegrity.Verify(plan, out _));
    }
    [Fact] public void ArrayOrderMattersAndDelimitersCannotAliasValues()
    {
        var first = Plan(); first["Fingerprints"] = new JArray("a|b", "c");
        var second = Plan(); second["Fingerprints"] = new JArray("a", "b|c");
        Assert.NotEqual(PlanIntegrity.Compute(first), PlanIntegrity.Compute(second));
        second["Fingerprints"] = new JArray("c", "a|b");
        Assert.NotEqual(PlanIntegrity.Compute(first), PlanIntegrity.Compute(second));
    }
    [Theory]
    [InlineData("{\"Digest\":\"a\",\"Digest\":\"b\"}")]
    [InlineData("{} {}")]
    public void AmbiguousJsonIsRejected(string json) => Assert.ThrowsAny<Exception>(() => PlanIntegrity.Parse(json));
    [Fact] public void FabricatedLabelCannotFreezeAnUnverifiedPlan()
    {
        var plan = Plan(); plan["Digest"] = new string('A', 64);
        var session = new SessionActivation();
        if (PlanIntegrity.Verify(plan, out _)) session.Freeze((string)plan["Digest"]!);
        Assert.Null(session.Digest);
    }
}
