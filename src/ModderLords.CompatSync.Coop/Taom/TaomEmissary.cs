using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// Elite emissary purchases for every player (TAOM-MAP P2).
///
/// TAOM lets a guest browse the emissary's offers, then refuses the purchase at the last step
/// (EliteEmissaryInquiryPresenter.ExecutePurchase: "The emissary only deals with the host"), because on a client the
/// resource charge would stick while the troops vanish on the next roster resync. On a client in a session this
/// component sends the purchase to the server instead, and the server runs TAOM's own EliteEmissaryService.Purchase
/// for that player (afford check, grant troops to their party, charge their resource), then tells them the outcome.
///
/// The server trusts only the troop and the quantity. Which emissary is being dealt with, and so which faction's offers
/// and currency apply, comes from where the player's party actually is on the server, through TAOM's own key-settlement,
/// access and owner checks.
/// </summary>
internal sealed class EmissaryComponent : ITaomComponent
{
    public const string Feature = "emissary";
    private const string Owner = "ModderLords.Taom.Emissary";

    private static MethodInfo? _executePurchase;
    private static MethodInfo? _purchase;
    private static MethodInfo? _isKeySettlement;
    private static MethodInfo? _accessBlocked;
    private static MethodInfo? _ownerInfo;
    private static Assembly? _taom;

    public string Id => "emissary";

    public string? SkipReason(TaomContext context)
    {
        var t = context.Taom;
        _taom = t;
        var owner = t.GetType("TAOM.Adapters.SettlementOwnerInfo", false);
        _executePurchase = owner == null ? null : t.GetType("TAOM.Features.EliteEmissary.Hooks.EliteEmissaryInquiryPresenter", false)
            ?.GetMethod("ExecutePurchase", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { owner, typeof(string), typeof(string), typeof(int) }, null);
        var service = t.GetType("TAOM.Features.EliteEmissary.IEliteEmissaryService", false);
        _purchase = service?.GetMethod("Purchase", new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(int) });
        _isKeySettlement = service?.GetMethod("IsKeySettlement", new[] { typeof(string) });
        _accessBlocked = t.GetType("TAOM.Features.EliteEmissary.Hooks.EliteEmissaryBehavior", false)
            ?.GetMethod("IsEmissaryAccessBlocked", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(Settlement) }, null);
        _ownerInfo = t.GetType("TAOM.Adapters.ISettlementOwnerAdapter", false)?.GetMethod("GetOwnerInfo", new[] { typeof(Settlement) });

        var missing = new List<string>();
        if (_executePurchase == null) missing.Add("EliteEmissaryInquiryPresenter.ExecutePurchase");
        if (_purchase == null) missing.Add("IEliteEmissaryService.Purchase");
        if (_isKeySettlement?.ReturnType != typeof(bool)) missing.Add("IEliteEmissaryService.IsKeySettlement");
        if (_accessBlocked?.ReturnType != typeof(bool)) missing.Add("EliteEmissaryBehavior.IsEmissaryAccessBlocked");
        if (_ownerInfo == null) missing.Add("ISettlementOwnerAdapter.GetOwnerInfo");
        return missing.Count == 0 ? null : "TAOM changed; not found: " + string.Join(", ", missing);
    }

    public string Install(TaomContext context)
    {
        if (context.IsServer)
        {
            TaomActions.Register(Feature, ServerBuy);
            return "server sells emissary troops to players";
        }
        new Harmony(Owner).Patch(_executePurchase!, prefix: new HarmonyMethod(typeof(EmissaryComponent), nameof(ExecutePurchasePrefix)));
        return "client sends emissary purchases to the server";
    }

    /// <summary>Client: replaces TAOM's "only deals with the host" refusal with a request to the server.</summary>
    private static bool ExecutePurchasePrefix(string troopId, int qty)
    {
        if (!TaomActions.IsCoopClient) return true;
        TaomActions.Send!(Feature, "buy", new[] { troopId, qty.ToString(CultureInfo.InvariantCulture) });
        Log.Info($"TAOM layer: emissary purchase of {qty}x '{troopId}' sent to the server");
        return false;
    }

    /// <summary>Server, game thread, inside PlayerScope.</summary>
    private static TaomActionOutcome ServerBuy(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (op != "buy" || args.Count != 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0 || qty > 1000)
            return TaomActionOutcome.Fail("The emissary did not understand the request.");
        var troopId = args[0];
        var settlement = party?.CurrentSettlement;
        if (settlement == null) return TaomActionOutcome.Fail("You must be in the settlement to deal with its emissary.");

        var service = TaomActions.Resolve(_taom!, "TAOM.Features.EliteEmissary.IEliteEmissaryService");
        var owners = TaomActions.Resolve(_taom!, "TAOM.Adapters.ISettlementOwnerAdapter");
        if (service == null || owners == null) return TaomActionOutcome.Fail("The emissary is not available on this server.");
        if (!(bool)_isKeySettlement!.Invoke(service, new object[] { settlement.StringId })!)
            return TaomActionOutcome.Fail("There is no emissary in this settlement.");
        if ((bool)_accessBlocked!.Invoke(null, new object[] { settlement })!)
            return TaomActionOutcome.Fail("The emissary will not deal with you here.");

        var owner = _ownerInfo!.Invoke(owners, new object[] { settlement })!;
        string? O(string name) => owner.GetType().GetProperty(name)?.GetValue(owner) as string;
        var result = _purchase!.Invoke(service, new object?[] { hero.StringId, O("OwnerKingdomId"), O("OwnerCultureId"), troopId, qty })!;
        var status = result.GetType().GetProperty("Status")?.GetValue(result)?.ToString();
        int I(string name) => result.GetType().GetProperty(name)?.GetValue(result) is int v ? v : 0;
        var resource = result.GetType().GetProperty("ResourceDisplayName")?.GetValue(result) as string ?? "";
        var troopName = MBObjectManager.Instance?.GetObject<CharacterObject>(troopId)?.Name?.ToString() ?? troopId;

        // The same lines TAOM shows for a host purchase.
        return status switch
        {
            "Success" => new TaomActionOutcome(true, new TextObject("{=taom_emissary_bought}Recruited {QTY} {TROOP} for {COST} {RESOURCE}.")
                .SetTextVariable("QTY", I("Quantity")).SetTextVariable("TROOP", troopName)
                .SetTextVariable("COST", I("TotalCost")).SetTextVariable("RESOURCE", resource).ToString()),
            "Unaffordable" => TaomActionOutcome.Fail(new TextObject("{=taom_emissary_cant_afford}Not enough {RESOURCE} — need {COST}.")
                .SetTextVariable("RESOURCE", resource).SetTextVariable("COST", I("TotalCost")).ToString()),
            "NoResource" => TaomActionOutcome.Fail(new TextObject("{=taom_emissary_no_resource}There is no emissary trade in this settlement.").ToString()),
            _ => TaomActionOutcome.Fail(new TextObject("{=taom_emissary_failed}The emissary could not complete the deal.").ToString()),
        };
    }
}
