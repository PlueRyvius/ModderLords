using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Hiring TAOM's named companions (Oaths of Service, new in TAOM 2.0.41) for every player.
///
/// TAOM names its price for a named companion ("I do not take the road for nothing. My price is ...") and then greys out
/// "It is agreed" on every co-op client ("Only the host can seal an oath in a shared campaign"): OathHireDialog.CanAccept
/// and HireTermsService.TryPayAndHire both refuse when ICoopSessionProvider.IsCoopClient. On a dedicated server there is
/// no host player, so nobody could hire Aragorn and the rest. Found by the hire-wanderer suite, 2026-11-22.
///
/// On a client in a session this component lets TAOM's own check run as if it were the authority (so the player still
/// sees TAOM's "You still need: ..." when short), and sends the accept to the server. The server runs TAOM's own
/// TryPayAndHire for that player (terms, payment, joining their clan and party) and tells them the outcome; Coop syncs
/// the companion's new clan and party back. The server trusts only which companion; the price and terms are its own.
/// </summary>
internal sealed class OathHireComponent : ITaomComponent
{
    public const string Feature = "oath-hire";
    private const string Owner = "ModderLords.Taom.OathHire";
    private const string ServiceType = "TAOM.Features.NamedCompanions.Oaths.HireTermsService";

    private static MethodInfo? _canAccept;
    private static MethodInfo? _pay;
    private static MethodInfo? _isCoopClient;
    private static MethodInfo? _evaluate;
    private static MethodInfo? _tryPayAndHire;
    private static Assembly? _taom;

    /// <summary>Set while TAOM's accept check runs on a client: TAOM then reads this peer as not a co-op client.</summary>
    [ThreadStatic] private static bool _checkingAsAuthority;

    public string Id => "oath-hire";

    public string? SkipReason(TaomContext context)
    {
        var t = context.Taom;
        _taom = t;
        var dialog = t.GetType("TAOM.Features.NamedCompanions.Oaths.Hooks.OathHireDialog", false);
        if (dialog == null) return "this TAOM has no oaths of service (before 2.0.41)";
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _canAccept = AccessTools.Method(dialog, "CanAccept");
        _pay = dialog.GetMethod("Pay", Instance, null, Type.EmptyTypes, null);
        _isCoopClient = AccessTools.PropertyGetter(t.GetType("TAOM.Features.CoopInterop.CoopSessionProvider", false), "IsCoopClient");
        var service = t.GetType(ServiceType, false);
        _evaluate = service?.GetMethod("Evaluate", new[] { typeof(string) });
        _tryPayAndHire = service?.GetMethod("TryPayAndHire", new[] { typeof(string), typeof(bool) });

        var missing = new List<string>();
        if (_canAccept?.ReturnType != typeof(bool)) missing.Add("OathHireDialog.CanAccept");
        if (_pay == null) missing.Add("OathHireDialog.Pay");
        if (_isCoopClient?.ReturnType != typeof(bool)) missing.Add("CoopSessionProvider.IsCoopClient");
        if (_evaluate == null) missing.Add("HireTermsService.Evaluate");
        if (_tryPayAndHire?.ReturnType != typeof(bool)) missing.Add("HireTermsService.TryPayAndHire");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerHire);
            return "server seals named companions' oaths for players";
        }
        var harmony = new Harmony(Owner);
        harmony.Patch(_canAccept!, prefix: new HarmonyMethod(typeof(OathHireComponent), nameof(CanAcceptPrefix)),
            finalizer: new HarmonyMethod(typeof(OathHireComponent), nameof(CanAcceptFinalizer)));
        harmony.Patch(_isCoopClient!, postfix: new HarmonyMethod(typeof(OathHireComponent), nameof(IsCoopClientPostfix)));
        harmony.Patch(_pay!, prefix: new HarmonyMethod(typeof(OathHireComponent), nameof(PayPrefix)));
        return "client sends named companions' oaths to the server";
    }

    private static void CanAcceptPrefix()
    {
        if (TaomActions.IsCoopClient) _checkingAsAuthority = true;
    }

    private static Exception? CanAcceptFinalizer(Exception? __exception)
    {
        _checkingAsAuthority = false;
        return __exception;
    }

    private static void IsCoopClientPostfix(ref bool __result)
    {
        if (_checkingAsAuthority) __result = false;
    }

    /// <summary>Client: "It is agreed" sends the hire to the server instead of TAOM's refusal.</summary>
    private static bool PayPrefix()
    {
        if (!TaomActions.IsCoopClient) return true;
        var companion = Hero.OneToOneConversationHero;
        if (companion == null) return false;
        TaomActions.Send!(Feature, "hire", new[] { companion.StringId });
        Log.Info($"TAOM layer: oath with '{companion.StringId}' sent to the server");
        return false;
    }

    /// <summary>Server, game thread, inside PlayerScope (TAOM's MainHero, MainParty and PlayerClan are the sender's).</summary>
    private static TaomActionOutcome ServerHire(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "hire" || args.Count != 1 || string.IsNullOrEmpty(args[0]))
            return TaomActionOutcome.Fail("The companion did not understand the offer.");
        if (party == null) return TaomActionOutcome.Fail("You have no party for a companion to join.");
        var companionId = args[0];
        var companion = Hero.FindFirst(h => h.StringId == companionId);
        if (companion == null || !companion.IsAlive) return TaomActionOutcome.Fail("That companion is not to be found.");
        if (companion.Clan != null && companion.Clan == hero.Clan) return TaomActionOutcome.Fail($"{companion.Name} already rides with you.");
        // Whether this companion takes oaths at all, and on what terms, is TAOM's own evaluation below.

        var service = TaomActions.Resolve(_taom!, ServiceType);
        if (service == null) return TaomActionOutcome.Fail("Oaths of service are not available on this server.");
        var evaluation = _evaluate!.Invoke(service, new object[] { companionId });
        var status = evaluation?.GetType().GetProperty("Status")?.GetValue(evaluation)?.ToString()
                     ?? evaluation?.GetType().GetField("Status")?.GetValue(evaluation)?.ToString();
        if (status != "Met")
            return TaomActionOutcome.Fail($"{companion.Name} will not take your oath yet: the terms are not met.");

        var hired = (bool)_tryPayAndHire!.Invoke(service, new object[] { companionId, false })!;
        Log.Info($"TAOM layer: oath of '{companionId}' for {hero.Name}: {(hired ? "sealed" : "refused by TAOM")}");
        return hired
            ? new TaomActionOutcome(true, $"{companion.Name} has joined your company.")
            : TaomActionOutcome.Fail($"{companion.Name} could not join you.");
    }
}
