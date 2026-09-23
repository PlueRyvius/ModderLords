using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's Enlistment in co-op (TAOM-MAP checklist). Read end to end on 2026-09-23: enlisting "parks" the player's party
/// (IsActive = false, IsVisible = false, pinned to the lord's position every frame) and the player then fights inside
/// the lord's formation. Under Coop the player's party state and who spawns in a battle belong to Coop itself, so this
/// cannot be made to work from this layer. TAOM already refuses the oath on a non-host peer, but silently: the lord
/// welcomes you to the column and nothing happens. On a co-op client the offer is now shown greyed out, with the reason.
/// </summary>
internal sealed class EnlistmentGuardComponent : ITaomComponent
{
    private static MethodInfo? _clickable;

    public string Id => "enlistment-guard";

    public string? SkipReason(TaomContext context)
    {
        if (context.IsServer) return "server";
        _clickable = context.Taom.GetType("TAOM.Features.Enlistment.Hooks.EnlistmentDialogBehavior", false)
            ?.GetMethod("OfferIsClickable", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(TextObject).MakeByRefType() }, null);
        return _clickable == null ? "TAOM changed; not found: EnlistmentDialogBehavior.OfferIsClickable(out TextObject)" : null;
    }

    public string Install(TaomContext context)
    {
        new Harmony("ModderLords.Taom.EnlistmentGuard").Patch(_clickable!,
            postfix: new HarmonyMethod(typeof(EnlistmentGuardComponent), nameof(Postfix)));
        return "enlisting is shown as unavailable in co-op (TAOM parks the player's party, which Coop owns)";
    }

    private static void Postfix(ref bool __result, ref TextObject? explanation)
    {
        if (!TaomActions.IsCoopClient) return;
        __result = false;
        explanation = new TextObject("{=!}Enlisting is not available in co-op: it takes your party off the map, and in co-op your party belongs to the shared game.");
    }
}
