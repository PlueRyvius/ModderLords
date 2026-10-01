using ModderLords.CompatSync.Coop.Fourberie;

namespace ModderLords.Core.Tests;

public sealed class FbPromptWireTests
{
    [Fact]
    public void AYesNoPromptRoundTrips()
    {
        var q = new FbInquiry
        {
            Id = 7, Title = "Dirty business", Text = "Ein Bote aus Vlandia ☠ — 「依頼」", AffirmativeText = "Okay", NegativeText = "",
            AffirmativeShown = true, NegativeShown = false, AffirmativeEnabled = false, AffirmativeHint = "not enough gold",
        };
        var back = Assert.IsType<FbInquiry>(FbPromptWire.Unpack(FbPromptWire.Pack(q)));
        Assert.Equal(7, back.Id);
        Assert.Equal(q.Text, back.Text);
        Assert.True(back.AffirmativeShown);
        Assert.False(back.NegativeShown);
        Assert.False(back.AffirmativeEnabled);
        Assert.Equal("not enough gold", back.AffirmativeHint);
    }

    [Fact]
    public void AListPromptRoundTripsWithItsElementsInOrder()
    {
        var q = new FbMultiInquiry { Id = 3, Title = "Give up a fief", Min = 1, Max = 2, ExitShown = false };
        q.Elements.Add(("Pravend", "town", true));
        q.Elements.Add(("Jaculan", "", false));
        var back = Assert.IsType<FbMultiInquiry>(FbPromptWire.Unpack(FbPromptWire.Pack(q)));
        Assert.Equal(new[] { ("Pravend", "town", true), ("Jaculan", "", false) }, back.Elements);
        Assert.Equal((1, 2), (back.Min, back.Max));
    }

    [Fact]
    public void NoticesAndConversationsRoundTrip()
    {
        var n = Assert.IsType<FbMapNotice>(FbPromptWire.Unpack(FbPromptWire.Pack(new FbMapNotice { NotifType = "victim", ActionType = "murder", ActionStringId = "lord_1_1", Description = "Your clan was a victim of a scheme!" })));
        Assert.Equal("murder", n.ActionType);
        var c = new FbConversation { CharacterId = "imperial_elite_cataphract" };
        c.Variables.Add(("CONTR_GOLD", "We will pay you 70000 upon completion."));
        var back = Assert.IsType<FbConversation>(FbPromptWire.Unpack(FbPromptWire.Pack(c)));
        Assert.Equal("imperial_elite_cataphract", back.CharacterId);
        Assert.Equal(c.Variables, back.Variables);
    }

    public static TheoryData<string[]> Malformed => new()
    {
        new[] { "inquiry", "x", "", "", "", "", "1", "0", "1", "", "1", "" },   // id not a number
        new[] { "inquiry", "1", "", "", "", "", "yes", "0", "1", "", "1", "" },   // flag not 0/1
        new[] { "inquiry", "1" },   // short
        new[] { "multi", "1", "", "", "", "", "0", "2", "1", "0" },   // min above max
        new[] { "multi", "1", "", "", "", "", "0", "0", "1", "2", "a", "", "1" },   // says 2 elements, has 1
        new[] { "conversation", "", "0" },   // no partner
        new[] { "conversation", "x", "1", "ONLY_NAME" },   // half a variable
        new[] { "wat" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedMessagesAreRefused(string[] message) => Assert.Null(FbPromptWire.Unpack(message));

    [Fact]
    public void OverlongTextIsCutWhenSentAndRefusedWhenReceived()
    {
        var packed = FbPromptWire.Pack(new FbInquiry { Id = 1, Text = new string('x', FbPromptWire.MaxText + 50) });
        Assert.Equal(FbPromptWire.MaxText, packed[3].Length);
        packed[3] += "x";
        Assert.Null(FbPromptWire.Unpack(packed));
    }

    [Fact]
    public void YesAndNoAnswersAreRead()
    {
        Assert.True(FbPromptWire.TryReadAnswer(FbPromptWire.Answer(5, true), out var id, out var yes, out var picked));
        Assert.Equal((5, true), (id, yes));
        Assert.Null(picked);
        Assert.True(FbPromptWire.TryReadAnswer(FbPromptWire.Answer(5, false), out _, out yes, out _));
        Assert.False(yes);
    }

    [Fact]
    public void AListAnswerMustFitThePromptItAnswers()
    {
        Assert.True(FbPromptWire.TryReadAnswer(FbPromptWire.AnswerPicked(2, new[] { 1, 0 }), out _, out var yes, out var picked, elementCount: 3, min: 1, max: 2));
        Assert.True(yes);
        Assert.Equal(new[] { 1, 0 }, picked);
        Assert.False(FbPromptWire.TryReadAnswer(FbPromptWire.AnswerPicked(2, new[] { 3 }), out _, out _, out _, 3, 1, 2));        // out of range
        Assert.False(FbPromptWire.TryReadAnswer(FbPromptWire.AnswerPicked(2, new[] { 1, 1 }), out _, out _, out _, 3, 1, 2));     // duplicate
        Assert.False(FbPromptWire.TryReadAnswer(FbPromptWire.AnswerPicked(2, new[] { 0, 1, 2 }), out _, out _, out _, 3, 1, 2));  // too many
        Assert.False(FbPromptWire.TryReadAnswer(FbPromptWire.AnswerPicked(2, Array.Empty<int>()), out _, out _, out _, 3, 1, 2)); // too few
        Assert.False(FbPromptWire.TryReadAnswer(new[] { "2", "maybe" }, out _, out _, out _));
        Assert.False(FbPromptWire.TryReadAnswer(new[] { "2", "yes", "extra" }, out _, out _, out _));
    }
}
