using System;
using System.Collections.Generic;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Which game menus cannot be opened without a current settlement. Kept free of game types so the rule is tested
/// without the game; <see cref="LoadMenuGuard"/> applies it.
/// </summary>
public static class LoadMenuGuardPolicy
{
    /// <summary>
    /// The menus whose initialisation reads <c>Settlement.CurrentSettlement</c> without a null check: the ones
    /// <c>EncounterGameMenuBehavior.encounter_menu_ui_castle_on_init</c> is registered for in 1.4.8.
    /// </summary>
    public static readonly HashSet<string> KnownSettlementMenus = new HashSet<string>(StringComparer.Ordinal)
    {
        "castle_outside", "castle_guard", "town_outside", "fortification_crime_rating", "village_outside",
        "menu_sneak_into_town_succeeded", "disguise_first_time", "disguise_not_first_time",
    };

    /// <summary>
    /// True for a menu that only makes sense inside a settlement: the known list, and every town, castle and village
    /// menu (town, town_backstreet, castle_dungeon, village_hostile_action, …), which all read the settlement too.
    /// </summary>
    public static bool NeedsSettlement(string? menuId)
    {
        if (string.IsNullOrEmpty(menuId)) return false;
        if (KnownSettlementMenus.Contains(menuId!)) return true;
        foreach (var prefix in new[] { "town", "castle", "village" })
            if (menuId!.Equals(prefix, StringComparison.Ordinal) || menuId.StartsWith(prefix + "_", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Whether the stored menu should be dropped instead of reopened.</summary>
    public static bool ShouldDrop(string? menuId, bool hasCurrentSettlement) => !hasCurrentSettlement && NeedsSettlement(menuId);
}
