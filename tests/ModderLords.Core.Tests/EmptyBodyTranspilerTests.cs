using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ModderLords.Core.Tests;

/// <summary>
/// The TAOM layer replaces TAOM's camp-visual method bodies on a dedicated server (TaomFieldCamp.EmptyBodyTranspiler),
/// because their original IL references SandBox.View, which the server cannot load: a skipping prefix would still
/// compile that IL. This pins the technique with real Harmony: after patching, the original body never runs and the
/// method answers false / returns.
/// </summary>
public sealed class EmptyBodyTranspilerTests
{
    public sealed class Visuals
    {
        public int OriginalRuns;
        public bool Show(string id) { OriginalRuns++; return true; }
        public void Remove(string id) { OriginalRuns++; }
    }

    // Same body as TaomFieldCamp.EmptyBodyTranspiler (that file compiles against game assemblies this project lacks).
    public static IEnumerable<CodeInstruction> EmptyBody(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (original is MethodInfo m && m.ReturnType == typeof(bool))
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    [Fact]
    public void ReplacedBodiesNeverRunTheOriginal()
    {
        var harmony = new Harmony("tests.empty-body-transpiler");
        try
        {
            foreach (var name in new[] { nameof(Visuals.Show), nameof(Visuals.Remove) })
                harmony.Patch(typeof(Visuals).GetMethod(name)!, transpiler: new HarmonyMethod(typeof(EmptyBodyTranspilerTests), nameof(EmptyBody)));
            var v = new Visuals();
            Assert.False(v.Show("party"));
            v.Remove("party");
            Assert.Equal(0, v.OriginalRuns);
        }
        finally { harmony.UnpatchAll("tests.empty-body-transpiler"); }
    }
}
