using HarmonyLib;

namespace ModderLords.Core.Tests;

/// <summary>
/// EncounterOptionGate.JoinConditionFinalizer relies on a Harmony finalizer catching an exception thrown by ANOTHER
/// owner's postfix (Coop's raid-join postfix reads PlayerEncounter.EncounteredBattle unguarded). Pinned with real Harmony.
/// </summary>
public sealed class FinalizerCatchesPostfixTests
{
    public sealed class Menu
    {
        public bool Condition() => true;
    }

    public static void ThrowingPostfix() => throw new NullReferenceException("battle not here yet");

    public static Exception? Finalizer(Exception? __exception, ref bool __result)
    {
        if (__exception is not NullReferenceException) return __exception;
        __result = false;
        return null;
    }

    [Fact]
    public void FinalizerSwallowsAPostfixExceptionAndSetsTheResult()
    {
        var coop = new Harmony("tests.other-owner");
        var ours = new Harmony("tests.finalizer");
        var target = typeof(Menu).GetMethod(nameof(Menu.Condition))!;
        try
        {
            coop.Patch(target, postfix: new HarmonyMethod(typeof(FinalizerCatchesPostfixTests), nameof(ThrowingPostfix)));
            Assert.Throws<NullReferenceException>(() => new Menu().Condition());
            ours.Patch(target, finalizer: new HarmonyMethod(typeof(FinalizerCatchesPostfixTests), nameof(Finalizer)));
            Assert.False(new Menu().Condition());
        }
        finally
        {
            coop.UnpatchAll("tests.other-owner");
            ours.UnpatchAll("tests.finalizer");
        }
    }
}
