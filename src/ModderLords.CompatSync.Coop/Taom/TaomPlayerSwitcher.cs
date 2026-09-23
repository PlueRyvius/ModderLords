using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's Player Switcher in co-op (TAOM-MAP checklist: Player Switcher).
///
/// The switcher is a hero picker TAOM attaches to the character-creation face screen: pick an existing lord and you
/// play as him (ChangePlayerCharacterAction). On a co-op client that screen builds the character the player joins
/// with; Coop then creates that player's own hero on the server from it. Taking over an existing lord there would
/// hand Coop a hero that already belongs to the world (and to the AI), which its player identity cannot represent.
/// So on a co-op client the picker is not attached; the rest of character creation is unchanged. It is only ever
/// offered in character creation, so there is no mid-campaign switch to guard.
/// </summary>
internal sealed class PlayerSwitcherGuardComponent : ITaomComponent
{
    private static MethodInfo? _attach;

    public string Id => "player-switcher-guard";

    public string? SkipReason(TaomContext context)
    {
        if (context.IsServer) return "server";
        _attach = context.Taom.GetType("TAOM.Features.PlayerSwitcher.Hooks.Patch77_BodyGeneratorView_Constructor", false)
            ?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Postfix");
        return _attach == null ? "TAOM changed; not found: Patch77_BodyGeneratorView_Constructor.Postfix" : null;
    }

    public string Install(TaomContext context)
    {
        new Harmony("ModderLords.Taom.PlayerSwitcher").Patch(_attach!,
            prefix: new HarmonyMethod(typeof(PlayerSwitcherGuardComponent), nameof(SkipInCoop)));
        return "TAOM's hero picker is not offered in co-op character creation";
    }

    private static bool _logged;

    private static bool SkipInCoop()
    {
        if (!TaomActions.IsCoopClient && !Common.ModInformation.IsClient) return true;
        if (!_logged) { _logged = true; Log.Info("TAOM layer: hero picker hidden in co-op character creation (play an existing lord is not supported)"); }
        return false;
    }
}
