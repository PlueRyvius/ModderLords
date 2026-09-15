using System.Reflection.Metadata;

namespace ModderLords.Core.Compat.Authority;

/// <summary>
/// "Is this the player's?" comparisons a server can rewrite to "is this any player's?": <c>Hero.MainHero</c>,
/// <c>Clan.PlayerClan</c> or <c>MobileParty/PartyBase.MainParty</c>, optionally through one property
/// (<c>Hero.MainHero.Clan</c>), immediately compared with <c>==</c>, <c>!=</c>, <c>Equals</c> or a branch. The other
/// operand has to be on the stack first (<c>owner == Hero.MainHero</c>); the getter-first form is left alone.
/// The same table lives in ModderLords.CompatSync.Coop's PlayerComparisonRewriter (net472, no shared reference);
/// PlayerComparisonTests checks that both count the same comparisons.
/// </summary>
public static class PlayerComparisonShapes
{
    public enum Kind { Hero, Clan, Party, PartyBase }

    public static Kind? Getter(string declaringType, string name)
    {
        if (!declaringType.StartsWith("TaleWorlds.", StringComparison.Ordinal)) return null;
        var simple = declaringType[(declaringType.LastIndexOf('.') + 1)..];
        return (simple, name) switch
        {
            ("Hero", "get_MainHero") => Kind.Hero,
            ("Clan", "get_PlayerClan") => Kind.Clan,
            ("MobileParty", "get_MainParty") => Kind.Party,
            ("PartyBase", "get_MainParty") => Kind.PartyBase,
            _ => null,
        };
    }

    /// <summary>One property step from the player's object to another object of the player's.</summary>
    public static Kind? Chain(Kind from, string name) => (from, name) switch
    {
        (Kind.Hero, "get_Clan") => Kind.Clan,
        (Kind.Hero, "get_PartyBelongedTo") => Kind.Party,
        (Kind.Party, "get_Party") => Kind.PartyBase,
        (Kind.Party, "get_LeaderHero") => Kind.Hero,
        (Kind.Party, "get_ActualClan") => Kind.Clan,
        (Kind.PartyBase, "get_MobileParty") => Kind.Party,
        (Kind.PartyBase, "get_LeaderHero") => Kind.Hero,
        _ => null,
    };

    public static bool IsComparison(ILOpCode op, string? calledName) =>
        op is ILOpCode.Ceq or ILOpCode.Beq or ILOpCode.Beq_s or ILOpCode.Bne_un or ILOpCode.Bne_un_s
        || calledName is "op_Equality" or "op_Inequality" or "Equals" or "ReferenceEquals";

    /// <summary>How many rewritable comparisons a method body holds.</summary>
    public static int Count(MetadataReader md, IReadOnlyList<IlInstruction> il)
    {
        static bool IsCall(ILOpCode op) => op is ILOpCode.Call or ILOpCode.Callvirt;
        var n = 0;
        for (var i = 0; i < il.Count; i++)
        {
            if (!IsCall(il[i].OpCode)) continue;
            var (type, name) = IlReader.MemberName(md, il[i].Operand);
            if (Getter(type, name) is not { } kind) continue;
            var j = i + 1;
            if (j < il.Count && IsCall(il[j].OpCode) && Chain(kind, IlReader.MemberName(md, il[j].Operand).name) is not null) j++;
            if (j >= il.Count) continue;
            var called = IsCall(il[j].OpCode) ? IlReader.MemberName(md, il[j].Operand).name : null;
            if (!IsComparison(il[j].OpCode, called)) continue;
            n++;
            i = j;
        }
        return n;
    }
}
