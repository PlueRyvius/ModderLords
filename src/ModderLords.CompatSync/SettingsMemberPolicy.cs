using System;
using System.Collections.Generic;
using System.Linq;

namespace ModderLords.CompatSync;

/// <summary>
/// Members of a discovered settings object that belong to each player and are never captured or applied, so the host's
/// value never lands on a player's game.
///
/// Key bindings, always: a hotkey is a player's own choice, and the host's binding replacing it is a bug whatever the
/// mod (Fourberie keeps 14 of them on the same Settings object as its gameplay switches). Anything else by name, from
/// the compat record's IgnoreSettingsTypes entries written as "Type.FullName::Member" (Fourberie's *OneButton
/// switches pick between a one-key and a two-key hotkey, also a player's own choice).
/// </summary>
public static class SettingsMemberPolicy
{
    public const string KeyBindingType = "TaleWorlds.InputSystem.InputKey";
    public const string MemberSeparator = "::";

    public static bool IsPersonal(Type settingsType, string memberName, Type valueType, IEnumerable<string> exclude)
    {
        var value = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (value.FullName == KeyBindingType) return true;
        var id = (settingsType.FullName ?? settingsType.Name) + MemberSeparator + memberName;
        return exclude.Any(p => string.Equals(p, id, StringComparison.Ordinal));
    }
}
