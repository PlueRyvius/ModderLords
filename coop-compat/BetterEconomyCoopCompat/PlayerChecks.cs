using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BetterEconomyCoopCompat;

/// <summary>
/// The two server-wide places where Living Economy asks "is this the player?" outside any one settlement
/// . The per-settlement ones are handled by <see cref="OwnerScope"/>.
///
/// - Lord wealth realism (WealthAuditCampaignBehavior.IsEligibleLordForWealthControl) trims the gold of lords above
///   a wealth target, skipping Hero.MainHero and Clan.PlayerClan. Its "is this the player?" comparisons are rewritten
///   to "is this any player?", so no human player's gold is trimmed.
/// - The AI treaty pass (TradeAgreementCampaignBehavior.RunAiTreatyPass) skips the player's kingdom, one kingdom.
///   While it runs, a kingdom a player leads is worth nothing to it (EvaluateAgreementValue answers 0), so the AI
///   never signs a treaty for a player; players sign their own through barter (Actions).
/// </summary>
internal sealed class PlayerChecks : IComponent
{
    private const string Owner = "modderlords.bettereconomy.coopcompat.PlayerChecks";
    private const string Wealth = "BetterEconomy.Behaviors.WealthAuditCampaignBehavior";
    private const string Treaty = "BetterEconomy.Behaviors.TradeAgreementCampaignBehavior";

    private MethodInfo? _eligible;
    private MethodInfo? _aiPass;
    private MethodInfo? _value;

    [ThreadStatic] private static bool _inAiPass;
    private static long _treatiesRefused;

    public string Id => "player-checks";

    public string? SkipReason(Context context)
    {
        if (!context.IsServer) return "client";
        _eligible = context.Method(Wealth, "IsEligibleLordForWealthControl", 1);
        _aiPass = context.Method(Treaty, "RunAiTreatyPass", 0);
        _value = context.Method(Treaty, "EvaluateAgreementValue", 2);
        var missing = new List<string>();
        if (_eligible?.ReturnType != typeof(bool)) missing.Add("WealthAuditCampaignBehavior.IsEligibleLordForWealthControl");
        if (_aiPass == null) missing.Add("TradeAgreementCampaignBehavior.RunAiTreatyPass");
        if (_value?.ReturnType != typeof(int)) missing.Add("TradeAgreementCampaignBehavior.EvaluateAgreementValue");
        return missing.Count == 3 ? "Living Economy changed; not found: " + string.Join(", ", missing) : null;
    }

    public string Install(Context context)
    {
        var parts = new List<string>();
        if (_eligible != null)
        {
            var t = typeof(Players);
            PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(Players.AnyPlayerHero))!, t.GetMethod(nameof(Players.AnyPlayerClan))!,
                t.GetMethod(nameof(Players.AnyPlayerParty))!, t.GetMethod(nameof(Players.AnyPlayerPartyBase))!);
            var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(new Harmony(Owner + ".Rewrite"),
                new[] { Wealth + "::IsEligibleLordForWealthControl" }, msg => Log.Warn(msg));
            parts.Add(comparisons >= 2
                ? "lord wealth realism leaves every player's gold alone"
                : $"lord wealth realism: only {comparisons} of 2 player check(s) rewritten ({methods} method(s), {missing} missing); players' gold may be trimmed");
        }
        if (_aiPass != null && _value != null)
        {
            var h = new Harmony(Owner);
            h.Patch(_aiPass, prefix: new HarmonyMethod(typeof(PlayerChecks), nameof(AiPassPrefix)),
                finalizer: new HarmonyMethod(typeof(PlayerChecks), nameof(AiPassFinalizer)));
            h.Patch(_value, prefix: new HarmonyMethod(typeof(PlayerChecks), nameof(ValuePrefix)));
            parts.Add("the AI makes no trade treaties for kingdoms players lead");
        }
        return string.Join("; ", parts);
    }

    private static void AiPassPrefix() => _inAiPass = true;

    private static Exception? AiPassFinalizer(Exception? __exception)
    {
        _inAiPass = false;
        return __exception;
    }

    private static bool ValuePrefix(Kingdom a, Kingdom b, ref int __result)
    {
        if (!_inAiPass || !Layer.IsCoopServer || !(Players.IsPlayerLedKingdom(a) || Players.IsPlayerLedKingdom(b))) return true;
        __result = 0;
        _treatiesRefused++;
        return false;
    }

    internal static long TreatiesRefused => _treatiesRefused;
}
