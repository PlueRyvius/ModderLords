using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Messengers on a co-op client: said out loud instead of silently doing nothing.
///
/// TAOM refuses MessengerCampaignBehavior.SendMessenger on a non-host peer, with a log line and nothing on screen, so a
/// click just does nothing. It is not relayed like the emissary: on arrival TAOM asks the player "Speak or Dismiss",
/// then fakes an encounter (PlayerEncounter.Start / EnterSettlement) and opens a conversation mission at the lord's
/// location. On a Coop client encounters and settlement entry are server-validated, so that step needs real encounter
/// work, not a relay (TAOM-MAP P2 notes). Until then the player is told.
/// </summary>
internal sealed class MessengerNoticeComponent : ITaomComponent
{
    private const string Owner = "ModderLords.Taom.Messengers";
    private static MethodInfo? _send;

    public string Id => "messenger-notice";

    public string? SkipReason(TaomContext context)
    {
        if (context.IsServer) return "server";
        _send = context.Taom.GetType("TAOM.Features.Messengers.MessengerCampaignBehavior", false)
            ?.GetMethod("SendMessenger", new[] { typeof(Hero) });
        return _send == null ? "TAOM changed; not found: MessengerCampaignBehavior.SendMessenger(Hero)" : null;
    }

    public string Install(TaomContext context)
    {
        new Harmony(Owner).Patch(_send!, prefix: new HarmonyMethod(typeof(MessengerNoticeComponent), nameof(SendPrefix)));
        return "client explains that messengers are not available in co-op";
    }

    private static bool SendPrefix()
    {
        if (!TaomActions.IsCoopClient) return true;
        TaomActions.ShowToPlayer(false, "Messengers are not available in co-op yet: no messenger was sent and no gold was spent.");
        return false;
    }
}
