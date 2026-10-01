using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop.Fourberie;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

// Stand-ins: "world actions" a mod calls (gold, relations), and the mod's own code that calls them from a menu, or from
// a handler the server runs itself.
internal static class StandInWorld
{
    public static readonly List<string> Recorded = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GiveGold(int amount) => Relation(1);   // an action that itself raises another one

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Relation(int change) { }

    public static void Prefix(MethodBase __originalMethod, ref bool __state)
    {
        __state = FbRecordGate.TryEnter();
        if (__state) Recorded.Add(__originalMethod.Name);
    }

    public static void Finalizer(bool __state)
    {
        if (__state) FbRecordGate.Leave();
    }
}

internal static class StandInModCode
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void MenuConsequence() => StandInWorld.GiveGold(100);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ServerRunTick() => StandInWorld.GiveGold(100);

}

public sealed class FbRecordGateTests : IDisposable
{
    private readonly Harmony _harmony = new("ModderLords.Tests.FbRecordGate");

    public FbRecordGateTests()
    {
        StandInWorld.Recorded.Clear();
        FbRecordGate.Mod = typeof(StandInModCode).Assembly;
        FbRecordGate.IsCoopClient = () => true;
        var prefix = new HarmonyMethod(typeof(StandInWorld), nameof(StandInWorld.Prefix));
        var finalizer = new HarmonyMethod(typeof(StandInWorld), nameof(StandInWorld.Finalizer));
        _harmony.Patch(typeof(StandInWorld).GetMethod(nameof(StandInWorld.GiveGold)), prefix: prefix, finalizer: finalizer);
        _harmony.Patch(typeof(StandInWorld).GetMethod(nameof(StandInWorld.Relation)), prefix: prefix, finalizer: finalizer);
        // A handler the server runs for the player: flagged on the player's game, as FbEffects does for every fanned-out handler.
        _harmony.Patch(typeof(StandInModCode).GetMethod(nameof(StandInModCode.ServerRunTick)),
            prefix: new HarmonyMethod(typeof(FbRecordGate), nameof(FbRecordGate.ServerRunPrefix)),
            finalizer: new HarmonyMethod(typeof(FbRecordGate), nameof(FbRecordGate.ServerRunFinalizer)));
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        FbRecordGate.Mod = null;
        FbRecordGate.IsCoopClient = null;
    }

    [Fact]
    public void AnActionFromTheModsOwnCodeIsRecordedOnceWithoutWhatItCausesItself()
    {
        StandInModCode.MenuConsequence();
        Assert.Equal(new[] { "GiveGold" }, StandInWorld.Recorded);   // not the Relation GiveGold raised: the replay does that
        Assert.False(FbRecordGate.Recording);
    }

    [Fact]
    public void AnActionUnderAHandlerTheServerRunsIsNotRecorded()
    {
        StandInModCode.ServerRunTick();
        Assert.Empty(StandInWorld.Recorded);
    }

    [Fact]
    public void AnActionThatDoesNotComeFromTheModIsNotRecorded()
    {
        FbRecordGate.Mod = typeof(JObject).Assembly;
        StandInModCode.MenuConsequence();
        Assert.Empty(StandInWorld.Recorded);
    }

    [Fact]
    public void NothingIsRecordedOutsideACoopClientSession()
    {
        FbRecordGate.IsCoopClient = () => false;
        StandInModCode.MenuConsequence();
        Assert.Empty(StandInWorld.Recorded);
    }

    [Fact]
    public void AfterAServerRunHandlerEndsRecordingWorksAgain()
    {
        StandInModCode.ServerRunTick();
        StandInModCode.MenuConsequence();
        Assert.Equal(new[] { "GiveGold" }, StandInWorld.Recorded);
    }
}

public sealed class FbEffectWireTests
{
    [Fact]
    public void OpsRoundTrip()
    {
        var ops = new List<FbEffectWire.Op>
        {
            FbEffectWire.Call("TaleWorlds.CampaignSystem.Actions.GiveGoldAction::ApplyInternal", new JArray("lord_1", null, "Player", "p:main", 500, true, "")),
            FbEffectWire.Delta("town", "Security", "town_A6", -5f),
            new() { Kind = "troops", Key = "m", Target = "imperial_recruit", Amount = 3, Wounded = 1, Xp = 40 },
            new() { Kind = "items", Target = "grain", Modifier = "", Amount = -2 },
        };
        var back = FbEffectWire.Unpack(FbEffectWire.Pack(ops))!;
        Assert.Equal(4, back.Count);
        Assert.Equal(500, (int)back[0].Args[4]);
        Assert.Equal(("town:Security", "town_A6", -5f), (back[1].Key, back[1].Target, back[1].Amount));
        Assert.Equal((3f, 1, 40), (back[2].Amount, back[2].Wounded, back[2].Xp));
        Assert.Equal(-2f, back[3].Amount);
    }

    [Theory]
    [InlineData("""[{"k":"delta","c":"town:Security","t":"town_A6","n":5000}]""")]       // beyond the bound
    [InlineData("""[{"k":"delta","c":"town:Gold","t":"town_A6","n":1}]""")]             // not a delta target
    [InlineData("""[{"k":"delta","c":"hero:Security","t":"x","n":1}]""")]               // not a target kind
    [InlineData("""[{"k":"troops","c":"m","t":"recruit","n":1.5}]""")]                  // half a troop
    [InlineData("""[{"k":"troops","c":"q","t":"recruit","n":1}]""")]                    // no such roster
    [InlineData("""[{"k":"launch","c":"x"}]""")]                                        // unknown kind
    [InlineData("""{"k":"call"}""")]                                                    // not a list
    [InlineData("not json")]
    public void AnyBadOpRefusesTheWholeBatch(string payload) => Assert.Null(FbEffectWire.Unpack(payload));

    [Fact]
    public void TroopsTakenOutForAMissionAndPutBackNetToNothing()
    {
        var net = new FbRosterNet();
        net.Troops("m", "imperial_recruit", -12, -2, 0);   // TroopCutCopy before the mission
        net.Items("grain", "", 5);                          // loot
        net.Troops("m", "imperial_recruit", 12, 2, 300);    // put back after, with the XP they earned
        var ops = net.Drain();
        Assert.Equal(2, ops.Count);
        Assert.Equal(("troops", 0f, 0, 300), (ops[0].Kind, ops[0].Amount, ops[0].Wounded, ops[0].Xp));
        Assert.Equal(("items", 5f), (ops[1].Kind, ops[1].Amount));
        Assert.True(net.IsEmpty);
        net.Troops("m", "x", 1, 0, 0);
        net.Troops("m", "x", -1, 0, 0);
        Assert.Empty(net.Drain());
    }
}

// A number declared on a base type and reached through a derived one, as Fief.FoodStocks is through Town.
internal class StandInFief
{
    public float FoodStocks { [MethodImpl(MethodImplOptions.NoInlining)] get; [MethodImpl(MethodImplOptions.NoInlining)] set; }
}

internal sealed class StandInTown : StandInFief { }

public sealed class FbEffectsSetterTests : IDisposable
{
    private readonly Harmony _harmony = new("ModderLords.Tests.FbEffectsSetter");
    private static int _seen;

    public static void Seen() => _seen++;

    public void Dispose() => _harmony.UnpatchAll(_harmony.Id);

    [Fact]
    public void PatchingTheSetterAsReachedThroughTheDerivedTypeFails()
    {
        // The bug: the setter looked up on the derived type is refused by Harmony, which took every later patch with it.
        Assert.ThrowsAny<Exception>(() => _harmony.Patch(AccessTools.PropertySetter(typeof(StandInTown), nameof(StandInFief.FoodStocks)),
            prefix: new HarmonyMethod(typeof(FbEffectsSetterTests), nameof(Seen))));
    }

    [Fact]
    public void TheDeclaredSetterPatchesAndSeesWritesThroughTheDerivedType()
    {
        var setter = FbPatchTargets.DeclaredSetter(typeof(StandInTown), nameof(StandInFief.FoodStocks));
        Assert.NotNull(setter);
        Assert.Equal(typeof(StandInFief), setter!.DeclaringType);
        _harmony.Patch(setter, prefix: new HarmonyMethod(typeof(FbEffectsSetterTests), nameof(Seen)));
        _seen = 0;
        new StandInTown().FoodStocks = 5f;
        Assert.Equal(1, _seen);
    }

    [Fact]
    public void ASetterDeclaredOnTheTypeItselfIsUnchanged()
    {
        Assert.Equal(typeof(StandInFief), FbPatchTargets.DeclaredSetter(typeof(StandInFief), nameof(StandInFief.FoodStocks))!.DeclaringType);
        Assert.Null(FbPatchTargets.DeclaredSetter(typeof(StandInTown), "NoSuchNumber"));
    }
}
