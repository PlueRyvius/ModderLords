using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Fief elections in a TAOM co-op kingdom (TAOM-MAP checklist: FiefGranting).
///
/// When a kingdom takes a town or castle, its clans vote on the new owner (Kingdom Decisions). TAOM replaces vanilla's
/// SettlementClaimantDecision with its own subclass, TaomSettlementClaimantDecision, to change the scoring (clans that
/// fought get a claim, big holders are damped). It does this on the server. Coop sends a kingdom decision to clients
/// through a converter keyed by the decision's EXACT type, and throws "Type of kingdom decision ... is not supported"
/// for anything else, so players in a TAOM kingdom never got the vote.
///
/// Both sides: a decision whose exact type Coop does not know is converted as its nearest base type Coop does know.
/// Clients receive an ordinary fief election (their screen shows vanilla's support numbers); the server keeps TAOM's
/// decision, so TAOM's scoring decides the outcome and players' votes count in it.
///
/// Server: TAOM's "exempt the player's clan from the penalties" setting compares each candidate with Clan.PlayerClan,
/// which on a dedicated server is the idle world-generation clan, so no player's clan was ever exempt. Each candidate's
/// facts are now built with that candidate's player (if any) as TAOM's player.
/// </summary>
internal sealed class FiefVoteSyncComponent : ITaomComponent
{
    private static MethodInfo? _convert;
    private static MethodInfo? _facts;
    private static FieldInfo? _table;

    public string Id => "fief-vote-sync";

    public string? SkipReason(TaomContext context)
    {
        var converter = AccessTools.TypeByName("GameInterface.Services.Kingdoms.KingdomDecisionDataConverter");
        _convert = converter == null ? null : AccessTools.Method(converter, "Convert");
        _table = converter == null ? null : AccessTools.Field(converter, "supportedConversions");
        _facts = context.IsServer
            ? context.Taom.GetType("TAOM.Features.FiefGranting.FiefGrantFactsBuilder", false)
                ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Build" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(TaleWorlds.CampaignSystem.Clan))
            : null;
        return _convert == null || _table == null || !typeof(IDictionary).IsAssignableFrom(_table.FieldType)
            ? "Coop's KingdomDecisionDataConverter.Convert / supportedConversions not found"
            : null;
    }

    public string Install(TaomContext context)
    {
        var h = new Harmony("ModderLords.Taom.FiefVote");
        h.Patch(_convert!, prefix: new HarmonyMethod(typeof(FiefVoteSyncComponent), nameof(Prefix)));
        if (_facts != null)
            h.Patch(_facts, prefix: new HarmonyMethod(typeof(FiefVoteSyncComponent), nameof(FactsPrefix)),
                finalizer: new HarmonyMethod(typeof(FiefVoteSyncComponent), nameof(FactsFinalizer)));
        else if (context.IsServer)
            Log.Warn("TAOM layer: FiefGrantFactsBuilder.Build(Clan, ...) not found; players' clans keep TAOM's AI fief-grant penalties");
        return "kingdom decisions of a TAOM subclass (fief elections) are sent as their vanilla base type";
    }

    private static void FactsPrefix(TaleWorlds.CampaignSystem.Clan clan, out IDisposable? __state)
    {
        __state = null;
        if (clan == null || ServerRelay.PlayerScope.Active) return;
        foreach (var p in PlayerContextComponent.Players())
            if (p.Hero.Clan == clan && p.Hero != TaleWorlds.CampaignSystem.Hero.MainHero)
            {
                __state = new ServerRelay.PlayerScope(p.Hero, p.Party);
                return;
            }
    }

    private static Exception? FactsFinalizer(IDisposable? __state, Exception? __exception)
    {
        __state?.Dispose();
        return __exception;
    }

    private static bool _logged;

    private static bool Prefix(object __instance, object kingdomDecision, ref object __result)
    {
        if (kingdomDecision == null || _table!.GetValue(__instance) is not IDictionary table) return true;
        var exact = kingdomDecision.GetType();
        if (table.Contains(exact)) return true;
        for (var t = exact.BaseType; t != null; t = t.BaseType)
        {
            if (!table.Contains(t) || table[t] is not Delegate convert) continue;
            if (!_logged) { _logged = true; Log.Info($"TAOM layer: {exact.Name} sent to clients as {t.Name}"); }
            __result = convert.DynamicInvoke(kingdomDecision)!;
            return false;
        }
        return true;   // Coop reports the unsupported type as before
    }
}
