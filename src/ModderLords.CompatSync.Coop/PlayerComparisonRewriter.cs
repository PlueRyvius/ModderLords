using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Recipe schema v3, server side: rewrites "is this the player's?" in the listed mod methods to "is this any player's?".
/// <c>owner == Hero.MainHero</c> becomes <c>IsPlayerHero(owner)</c>; on a Coop server <c>Hero.MainHero</c> is the host,
/// so a single-player mod would otherwise treat every player's castle, clan or party as an NPC's.
/// Harmony only (the "any player" helpers are supplied), so the test project runs it against plain methods. The shape
/// table matches ModderLords.Core's PlayerComparisonShapes; PlayerComparisonTests checks both count the same.
/// </summary>
public static class PlayerComparisonRewriter
{
    public enum Kind { Hero, Clan, Party, PartyBase }

    private static readonly Dictionary<Kind, MethodInfo> s_helpers = new Dictionary<Kind, MethodInfo>();
    private static readonly Dictionary<string, int> s_rewritten = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly HashSet<string> s_patched = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Static <c>bool (T)</c> methods answering "does this belong to any player?" for each kind.</summary>
    public static void SetHelpers(MethodInfo hero, MethodInfo clan, MethodInfo party, MethodInfo partyBase)
    {
        s_helpers[Kind.Hero] = hero;
        s_helpers[Kind.Clan] = clan;
        s_helpers[Kind.Party] = party;
        s_helpers[Kind.PartyBase] = partyBase;
    }

    /// <summary>Installs the rewrite on each method (idempotent). Returns methods patched, comparisons rewritten, and ids not found.</summary>
    public static (int methods, int comparisons, int missing) Apply(Harmony harmony, IEnumerable<string> methodIds, Action<string> warn)
    {
        int methods = 0, comparisons = 0, missing = 0;
        var transpiler = new HarmonyMethod(typeof(PlayerComparisonRewriter).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.Public));
        foreach (var id in methodIds.Distinct(StringComparer.Ordinal))
        {
            if (s_patched.Contains(id)) continue;
            var targets = RecipeGates.Resolve(id);
            if (targets.Count == 0) { missing++; warn("player checks: method not found: " + id); continue; }
            try
            {
                foreach (var m in targets)
                {
                    harmony.Patch(m, transpiler: transpiler);
                    comparisons += Rewritten(m);
                }
                s_patched.Add(id);
                methods++;
            }
            catch (Exception ex) { missing++; warn("player checks: could not patch " + id + ": " + ex.GetBaseException().Message); }
        }
        return (methods, comparisons, missing);
    }

    private static string Key(MethodBase m) => (m.DeclaringType?.FullName ?? "?") + "::" + m.Name + "/" + m.GetParameters().Length;

    /// <summary>Comparisons the transpiler rewrote in that method (0 before it is patched).</summary>
    public static int Rewritten(MethodBase m)
    {
        lock (s_rewritten) return s_rewritten.TryGetValue(Key(m), out var n) ? n : 0;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var list = instructions.ToList();
        var output = new List<CodeInstruction>(list.Count);
        var n = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (TryRewrite(list, i, output, out var consumed))
            {
                i += consumed - 1;
                n++;
                continue;
            }
            output.Add(list[i]);
        }
        lock (s_rewritten) s_rewritten[Key(__originalMethod)] = n;
        return output;
    }

    private static bool TryRewrite(List<CodeInstruction> list, int i, List<CodeInstruction> output, out int consumed)
    {
        consumed = 0;
        if (Getter(list[i]) is not { } kind) return false;
        var j = i + 1;
        if (j < list.Count && Called(list[j]) is { } step && Chain(kind, step.Name) is { } chained)
        {
            kind = chained;
            j++;
        }
        if (j >= list.Count || !s_helpers.TryGetValue(kind, out var helper)) return false;

        var cmp = list[j];
        var called = Called(cmp)?.Name;
        var replacement = new List<CodeInstruction> { new CodeInstruction(OpCodes.Call, helper) };
        if (cmp.opcode == OpCodes.Ceq || called is "op_Equality" or "Equals" or "ReferenceEquals")
        {
            // [other, player] == → [IsAnyPlayer(other)]
        }
        else if (called == "op_Inequality")
        {
            replacement.Add(new CodeInstruction(OpCodes.Ldc_I4_0));
            replacement.Add(new CodeInstruction(OpCodes.Ceq));
        }
        else if (cmp.opcode == OpCodes.Beq || cmp.opcode == OpCodes.Beq_S)
            replacement.Add(new CodeInstruction(cmp.opcode == OpCodes.Beq ? OpCodes.Brtrue : OpCodes.Brtrue_S, cmp.operand));
        else if (cmp.opcode == OpCodes.Bne_Un || cmp.opcode == OpCodes.Bne_Un_S)
            replacement.Add(new CodeInstruction(cmp.opcode == OpCodes.Bne_Un ? OpCodes.Brfalse : OpCodes.Brfalse_S, cmp.operand));
        else
            return false;

        // Labels and exception blocks on the replaced instructions move to the first new one, so jumps into it still land.
        for (var k = i; k <= j; k++)
        {
            replacement[0].labels.AddRange(list[k].labels);
            replacement[0].blocks.AddRange(list[k].blocks);
        }
        output.AddRange(replacement);
        consumed = j - i + 1;
        return true;
    }

    private static MethodInfo? Called(CodeInstruction ci) =>
        (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) ? ci.operand as MethodInfo : null;

    private static Kind? Getter(CodeInstruction ci)
    {
        var m = Called(ci);
        var type = m?.DeclaringType;
        if (m is null || type?.Namespace is null || !type.Namespace.StartsWith("TaleWorlds.", StringComparison.Ordinal)) return null;
        switch (type.Name + "::" + m.Name)
        {
            case "Hero::get_MainHero": return Kind.Hero;
            case "Clan::get_PlayerClan": return Kind.Clan;
            case "MobileParty::get_MainParty": return Kind.Party;
            case "PartyBase::get_MainParty": return Kind.PartyBase;
            default: return null;
        }
    }

    private static Kind? Chain(Kind from, string name)
    {
        switch (from + "." + name)
        {
            case "Hero.get_Clan": return Kind.Clan;
            case "Hero.get_PartyBelongedTo": return Kind.Party;
            case "Party.get_Party": return Kind.PartyBase;
            case "Party.get_LeaderHero": return Kind.Hero;
            case "Party.get_ActualClan": return Kind.Clan;
            case "PartyBase.get_MobileParty": return Kind.Party;
            case "PartyBase.get_LeaderHero": return Kind.Hero;
            default: return null;
        }
    }
}
