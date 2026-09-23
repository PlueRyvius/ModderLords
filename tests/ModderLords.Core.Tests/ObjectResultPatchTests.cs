using HarmonyLib;

namespace ModderLords.Core.Tests;

/// <summary>
/// The TAOM layer patches methods whose return types live in TAOM (RefugeData, SupplyOrder,
/// IReadOnlyCollection&lt;SupplyOrder&gt;), which it cannot name at compile time, so its patches take __result as
/// object or as IReadOnlyCollection&lt;object&gt;. Pinned with real Harmony: those shapes must read and replace the result.
/// </summary>
public sealed class ObjectResultPatchTests
{
    public sealed class Row { public string Id = ""; }

    public sealed class Book
    {
        public Row? Nearest() => new Row { Id = "other" };
        public IReadOnlyCollection<Row> Active() => new List<Row> { new() { Id = "mine" }, new() { Id = "other" } };
        public Row? Place(string id, ref string reason) { reason = "ran"; return null; }
    }

    public static void DropOthers(ref object? __result)
    {
        if (__result is Row { Id: "other" }) __result = null;
    }

    public static void OnlyMine(ref IReadOnlyCollection<object> __result) =>
        __result = new List<Row>(__result.Cast<Row>().Where(r => r.Id == "mine"));

    public static bool Relay(ref string reason, ref object __result)
    {
        reason = null!;
        __result = new Row { Id = "placeholder" };
        return false;
    }

    [Fact]
    public void ObjectResultCanBeCleared()
    {
        var h = new Harmony("tests.object-result");
        try
        {
            h.Patch(typeof(Book).GetMethod(nameof(Book.Nearest)), postfix: new HarmonyMethod(typeof(ObjectResultPatchTests), nameof(DropOthers)));
            Assert.Null(new Book().Nearest());
        }
        finally { h.UnpatchAll("tests.object-result"); }
    }

    [Fact]
    public void CovariantCollectionResultCanBeReplaced()
    {
        var h = new Harmony("tests.collection-result");
        try
        {
            h.Patch(typeof(Book).GetMethod(nameof(Book.Active)), postfix: new HarmonyMethod(typeof(ObjectResultPatchTests), nameof(OnlyMine)));
            Assert.Equal(new[] { "mine" }, new Book().Active().Select(r => r.Id));
        }
        finally { h.UnpatchAll("tests.collection-result"); }
    }

    [Fact]
    public void SkippingPrefixSetsObjectResultAndOutArgument()
    {
        var h = new Harmony("tests.relay-result");
        try
        {
            h.Patch(typeof(Book).GetMethod(nameof(Book.Place)), prefix: new HarmonyMethod(typeof(ObjectResultPatchTests), nameof(Relay)));
            var reason = "x";
            Assert.Equal("placeholder", new Book().Place("a", ref reason)?.Id);
            Assert.Null(reason);
        }
        finally { h.UnpatchAll("tests.relay-result"); }
    }
}
