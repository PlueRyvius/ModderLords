using System;
using System.Collections.Generic;
using System.Linq;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Launch;

/// <summary>
/// Settings sync, per mod: which mods' settings players keep for themselves (the Mods list's "Locked to host" unticked).
/// The server's ModderLords.Compat module reads the list from recipes.json (<c>PlayersOwnSettings</c>) and never sends
/// those mods' settings, MCM or their own classes, to players. Every other mod's settings stay locked to the host.
/// </summary>
public static class SettingsLockPolicy
{
    /// <summary>The selected mods whose "Locked to host" is unticked, in selection order.</summary>
    public static List<string> PlayersOwn(Profile profile, IEnumerable<string> selectedIds) =>
        selectedIds.Where(id => profile.Mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && m.LockSettingsToHost == false))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// A warning for each unlocked mod that relies on the host's settings reaching players: one whose ModderLords co-op
    /// support needs settings sync (TAOM, Living Economy, Fourberie), or one whose compat record sets values that only
    /// travel through it (DefaultSettings).
    /// </summary>
    public static IEnumerable<string> Warnings(IEnumerable<string> playersOwn, Func<string, bool> hasDefaultSettings)
    {
        foreach (var id in playersOwn)
        {
            var one = new[] { id };
            if (TaomLaunchPolicy.IsTaom(one) || LivingEconomyLaunchPolicy.IsLivingEconomy(one) || FourberieLaunchPolicy.IsFourberie(one))
                yield return $"{id}: its settings are not locked to the host, but ModderLords' co-op support for it expects every player to use the server's. Players with different values may see different results; tick Locked to host unless you are sure.";
            else if (hasDefaultSettings(id))
                yield return $"{id}: its settings are not locked to the host, so the values its compatibility record sets for co-op do not reach players.";
        }
    }
}
