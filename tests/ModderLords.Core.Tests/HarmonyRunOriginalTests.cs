using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ModderLords.Core.Tests;

// InventoryExchangeEvent raises the inventory event only when something skipped the original Done (Coop's prefix) and
// relies on Harmony telling a postfix so. Checked on the Harmony the game ships (2.4.2), with the same priority order.
public sealed class HarmonyRunOriginalTests : IDisposable
{
    private readonly Harmony _harmony = new("ModderLords.Tests.RunOriginal");
    private static bool _skip;
    private static readonly List<bool> Seen = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Done() => true;

    public static void Capture() { }
    public static bool Replace(ref bool __result) { if (!_skip) return true; __result = true; return false; }
    public static void Postfix(bool __runOriginal) => Seen.Add(__runOriginal);

    public HarmonyRunOriginalTests()
    {
        Seen.Clear();
        var target = typeof(HarmonyRunOriginalTests).GetMethod(nameof(Done));
        _harmony.Patch(target, prefix: new HarmonyMethod(typeof(HarmonyRunOriginalTests), nameof(Capture)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(HarmonyRunOriginalTests), nameof(Postfix)));
        _harmony.Patch(target, prefix: new HarmonyMethod(typeof(HarmonyRunOriginalTests), nameof(Replace)));
    }

    public void Dispose() => _harmony.UnpatchAll(_harmony.Id);

    [Fact]
    public void APostfixSeesThatAPrefixSkippedTheOriginal()
    {
        _skip = true;
        Assert.True(Done());
        Assert.Equal(new[] { false }, Seen);
    }

    [Fact]
    public void APostfixSeesThatTheOriginalRan()
    {
        _skip = false;
        Assert.True(Done());
        Assert.Equal(new[] { true }, Seen);
    }
}
