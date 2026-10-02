using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

public class DecisionFieldCodecTests
{
    private enum Office { Chancellor, Marshal = 7 }

    private class BaseDecision
    {
        private bool _isEnforced;
        public string? Note { get; private set; }
        public BaseDecision(bool enforced, string note) { _isEnforced = enforced; Note = note; }
        public bool IsEnforced => _isEnforced;
    }

    private sealed class ModDecision : BaseDecision
    {
        private readonly Office _office;
        private readonly float _controversy;
        private readonly List<string> _notSent = new() { "x" };
        public ModDecision(Office office, float controversy) : base(true, "proposed") { _office = office; _controversy = controversy; }
        public Office Office => _office;
        public float Controversy => _controversy;
        public List<string> NotSent => _notSent;
    }

    [Fact]
    public void A_decisions_own_and_inherited_fields_survive_the_trip()
    {
        var fields = DecisionFieldCodec.Encode(new ModDecision(Office.Marshal, 0.125f), out var skipped, (_, _) => null);
        var copy = (ModDecision)DecisionFieldCodec.Decode(typeof(ModDecision), fields, (_, _, _) => null);

        Assert.Equal(Office.Marshal, copy.Office);
        Assert.Equal(0.125f, copy.Controversy);
        Assert.True(copy.IsEnforced);
        Assert.Equal("proposed", copy.Note);
        Assert.Equal(new[] { "_notSent" }, skipped);
        Assert.Null(copy.NotSent);
    }

    [Fact]
    public void Same_named_fields_on_base_and_subclass_are_kept_apart()
    {
        var fields = DecisionFieldCodec.Encode(new ModDecision(Office.Chancellor, 1f), out _, (_, _) => null);
        Assert.Contains("ModDecision._office", fields);
        Assert.Contains("BaseDecision._isEnforced", fields);
        Assert.Contains("BaseDecision.<Note>k__BackingField", fields);
    }
}
