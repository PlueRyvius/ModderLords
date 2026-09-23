using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Carries a joining player's TAOM character-creation package to the server (TAOM-MAP P0.5).
///
/// Coop replaces the hero a joiner built with the copy the server adds to its world. TAOM's grants for that
/// character — culture starting gold, special-resource seed, career, race — were made against the local hero, and
/// TAOM's own PlayerPossession re-applies them after the swap, but only on the client: gold is Coop-synced and the
/// career and resource balances live in TAOM behaviour state the server never sees. This component records what the
/// player chose (TAOM already captures it: PlayerPossessionService.CaptureCharacterCreationChoices), sends it once the
/// campaign is ready, and has the server run TAOM's own JoinReconciliationService for that player's hero.
///
/// Once per hero, for good: the server adds the hero to the same persisted marker list TAOM keeps
/// (PlayerPossessionBehavior._reconciledHeroIds), so a reconnect or a restarted server does not grant twice.
/// </summary>
internal sealed class JoinGrantComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.JoinGrant";

    public string Id => "join-grant";

    public string? SkipReason(TaomContext context)
    {
        TaomJoinGrant.Bind(context.Taom);
        return TaomJoinGrant.MissingSurface;
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer) return "server applies players' character-creation packages when they join";
        new Harmony(Owner).Patch(TaomJoinGrant.CaptureMethod!,
            postfix: new HarmonyMethod(typeof(TaomJoinGrant), nameof(TaomJoinGrant.CapturePostfix)));
        return "client records this player's character-creation choices for the server";
    }
}

/// <summary>What the joining player picked. Plain strings so it travels without TAOM types.</summary>
internal sealed class JoinChoices
{
    public JoinChoices(string heroId, string cultureId, int raceId, string? careerId)
    {
        HeroId = heroId;
        CultureId = cultureId;
        RaceId = raceId;
        CareerId = careerId;
    }

    public string HeroId { get; }
    public string CultureId { get; }
    public int RaceId { get; }
    public string? CareerId { get; }

    public override string ToString() => $"culture={CultureId}, race={RaceId}, career={CareerId ?? "none"}";
}

internal static class TaomJoinGrant
{
    private static Type? _choicesType;
    private static ConstructorInfo? _choicesCtor;
    private static Type? _reconciliationType;
    private static MethodInfo? _reapply;
    private static MethodInfo? _resolve;
    private static Type? _behaviorType;
    private static FieldInfo? _reconciled;

    /// <summary>TAOM's PlayerPossessionService.CaptureCharacterCreationChoices, patched on clients.</summary>
    internal static MethodInfo? CaptureMethod { get; private set; }

    /// <summary>Null when every TAOM member this component uses is present with the expected shape.</summary>
    internal static string? MissingSurface { get; private set; } = "not bound";

    /// <summary>The choices captured in this process, waiting to be sent. Cleared once the server has answered.</summary>
    internal static JoinChoices? Pending { get; private set; }

    private static readonly HashSet<string> AppliedThisSession = new HashSet<string>(StringComparer.Ordinal);

    private static Assembly? _taom;

    internal static void Bind(Assembly taom)
    {
        _taom = taom;
        _choicesType = taom.GetType("TAOM.Features.PlayerPossession.PlayerCharacterCreationChoices", false);
        _choicesCtor = _choicesType?.GetConstructor(new[] { typeof(string), typeof(string), typeof(int), typeof(string) });
        var service = taom.GetType("TAOM.Features.PlayerPossession.PlayerPossessionService", false);
        CaptureMethod = _choicesType == null ? null : service?.GetMethod("CaptureCharacterCreationChoices", new[] { _choicesType });
        _reconciliationType = taom.GetType("TAOM.Features.PlayerPossession.IJoinReconciliationService", false);
        _reapply = _choicesType == null ? null
            : _reconciliationType?.GetMethod("ReapplyCharacterCreationPackage", new[] { _choicesType, typeof(string), typeof(string) });
        _resolve = taom.GetType("TAOM.IoC", false)?.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        _behaviorType = taom.GetType("TAOM.Features.PlayerPossession.PlayerPossessionBehavior", false);
        _reconciled = _behaviorType?.GetField("_reconciledHeroIds", BindingFlags.Instance | BindingFlags.NonPublic);

        var missing = new List<string>();
        if (_choicesCtor == null) missing.Add("PlayerCharacterCreationChoices(string, string, int, string)");
        if (CaptureMethod == null) missing.Add("PlayerPossessionService.CaptureCharacterCreationChoices");
        if (_reapply == null || _reapply.ReturnType != typeof(bool)) missing.Add("IJoinReconciliationService.ReapplyCharacterCreationPackage");
        if (_resolve == null || !_resolve.IsGenericMethodDefinition) missing.Add("TAOM.IoC.Resolve<T>()");
        if (_reconciled == null || _reconciled.FieldType != typeof(List<string>)) missing.Add("PlayerPossessionBehavior._reconciledHeroIds");
        MissingSurface = missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    /// <summary>Client, inside TAOM: character creation just finished and TAOM recorded what the player picked.</summary>
    internal static void CapturePostfix(object choices)
    {
        try
        {
            if (choices == null) return;
            var t = choices.GetType();
            string? S(string name) => t.GetProperty(name)?.GetValue(choices) as string;
            var heroId = S("HeroId");
            var culture = S("CultureId");
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(culture)) return;
            var race = t.GetProperty("RaceId")?.GetValue(choices) is int r ? r : -1;
            Pending = new JoinChoices(heroId!, culture!, race, S("CareerId"));
            Log.Info("TAOM layer: join-grant recorded this player's character-creation choices (" + Pending + ")");
        }
        catch (Exception ex) { Log.Warn("TAOM layer: join-grant could not record choices: " + ex.GetBaseException().Message); }
    }

    /// <summary>Client: the server answered, so nothing is left to send.</summary>
    internal static void Answered() => Pending = null;

    /// <summary>
    /// Client, game thread, once the server applied a fresh player's package: TAOM starts a new character at their
    /// culture's starting settlement (CharacterCreationContentService.TeleportToStartingSettlement), but that ran on the
    /// local character-creation campaign, and Coop places the joined party wherever it spawns players. The client moves
    /// its own party in Coop, so it goes to the culture's start here, as TAOM would have put it.
    /// </summary>
    internal static void PlaceAtStart(string cultureId)
    {
        try
        {
            if (_taom == null || MobileParty.MainParty == null) return;
            var provider = TaomActions.Resolve(_taom, "TAOM.Features.CharacterCreation.ICultureCreationDataProvider");
            var data = provider?.GetType().GetMethod("GetCultureData", new[] { typeof(string) })?.Invoke(provider, new object[] { cultureId });
            var settlementId = data?.GetType().GetProperty("StartingSettlement")?.GetValue(data) as string;
            if (string.IsNullOrEmpty(settlementId)) { Log.Info($"TAOM layer: culture '{cultureId}' has no starting settlement; party left where Coop placed it"); return; }
            var settlement = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(settlementId);
            if (settlement == null) { Log.Warn($"TAOM layer: starting settlement '{settlementId}' not found"); return; }
            var gate = settlement.GatePosition;
            MobileParty.MainParty.Position = gate.IsNonZero() ? gate : settlement.Position;
            Log.Info($"TAOM layer: party placed at {cultureId}'s starting settlement '{settlementId}'");
        }
        catch (Exception ex) { Log.Warn("TAOM layer: could not place the party at the culture's start: " + ex.GetBaseException().Message); }
    }

    /// <summary>
    /// Server, on the game thread: applies a joining player's package to their hero. Returns whether TAOM applied
    /// anything, and a line for both logs.
    /// </summary>
    internal static (bool Applied, string Detail) ServerApply(Hero hero, MobileParty? party, JoinChoices choices)
    {
        if (MissingSurface != null) return (false, "join-grant is off on the server: " + MissingSurface);
        if (Campaign.Current == null) return (false, "no campaign");
        if (hero == Hero.MainHero) return (false, "refused: that is the server's own hero");
        if (AppliedThisSession.Contains(hero.StringId)) return (false, $"'{hero.StringId}' was already reconciled this session");

        var markers = ReconciledMarkers();
        if (markers == null) return (false, "TAOM's PlayerPossessionBehavior is not running on the server");
        if (markers.Contains(hero.StringId)) return (false, $"'{hero.StringId}' was already reconciled (saved marker)");

        var service = _resolve!.MakeGenericMethod(_reconciliationType!).Invoke(null, null);
        if (service == null) return (false, "TAOM's join reconciliation service is not registered");
        var taomChoices = _choicesCtor!.Invoke(new object?[] { choices.HeroId, choices.CultureId, choices.RaceId, choices.CareerId });
        var kingdomId = hero.Clan?.Kingdom?.StringId;

        bool applied;
        using (new ServerRelay.PlayerScope(hero, party))
            applied = (bool)_reapply!.Invoke(service, new[] { taomChoices, hero.StringId, kingdomId })!;

        AppliedThisSession.Add(hero.StringId);
        if (applied) markers.Add(hero.StringId);
        var culture = hero.Culture?.StringId;
        var note = culture != null && !string.Equals(culture, choices.CultureId, StringComparison.OrdinalIgnoreCase)
            ? $" (hero's culture on the server is {culture}; TAOM grants by the culture the player picked)"
            : "";
        return (applied, applied
            ? $"applied TAOM's character-creation package to '{hero.StringId}' ({choices}){note}"
            : $"TAOM found nothing to apply for '{hero.StringId}' ({choices})");
    }

    private static List<string>? ReconciledMarkers()
    {
        var getter = typeof(Campaign).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var behavior = getter?.MakeGenericMethod(_behaviorType!).Invoke(Campaign.Current, null);
        return behavior == null ? null : _reconciled!.GetValue(behavior) as List<string>;
    }
}
