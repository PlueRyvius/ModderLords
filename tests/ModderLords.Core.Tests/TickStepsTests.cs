using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

public sealed class TickStepsTests
{
    private readonly List<string> _warnings = new();
    private readonly List<string> _ran = new();

    private void Tick(TickSteps steps, string failWith)
    {
        steps.Run("first", () => _ran.Add("first"));
        steps.Run("broken", () => throw new InvalidOperationException(failWith));
        steps.Run("last", () => _ran.Add("last"));
    }

    [Fact]
    public void AStepThatThrowsDoesNotSkipTheStepsAfterIt()
    {
        Tick(new TickSteps(_warnings.Add), "RelationMaterializedValues exceeds record bounds");
        Assert.Equal(new[] { "first", "last" }, _ran);
    }

    [Fact]
    public void ARepeatedFailureIsLoggedOnceAndCounted()
    {
        var steps = new TickSteps(_warnings.Add);
        for (var i = 0; i < 50; i++) Tick(steps, "same problem");
        Assert.Single(_warnings);
        Assert.Contains("'broken'", _warnings[0]);
        Assert.Equal("tick steps failing broken x50", steps.Summary());
    }

    [Fact]
    public void ANewMessageFromTheSameStepIsLoggedAgain()
    {
        var steps = new TickSteps(_warnings.Add);
        Tick(steps, "one");
        Tick(steps, "one");
        Tick(steps, "two");
        Assert.Equal(2, _warnings.Count);
        Assert.Contains("two", _warnings[1]);
    }

    [Fact]
    public void NoFailuresSaysSo()
    {
        var steps = new TickSteps(_warnings.Add);
        steps.Run("fine", () => { });
        Assert.Equal("tick steps failing 0", steps.Summary());
        Assert.Empty(_warnings);
    }
}
