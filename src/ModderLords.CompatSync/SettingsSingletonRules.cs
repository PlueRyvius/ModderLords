using System;
using System.Linq;

namespace ModderLords.CompatSync;

/// <summary>
/// When a static singleton counts as a settings object, and when to stop waiting for one (StaticSettingsSource).
/// No game references, so the unit tests can check it.
/// </summary>
internal static class SettingsSingletonRules
{
    /// <summary>The static members a settings object is reached through (X.Instance, X.Current, ...).</summary>
    public static readonly string[] SingletonNames = { "Instance", "Current", "Settings", "Config", "Default" };

    /// <summary>
    /// The singleton names that are evidence on their own, for a class whose name does not look like settings.
    /// "Current" and "Default" are not: they usually mean the game object in play. Retinues' WSettlement.Current (the
    /// settlement the player is in) was read every 3 s as a possible settings object, and threw on the map.
    /// </summary>
    public static readonly string[] EvidenceByThemselves = { "Instance", "Settings", "Config" };

    /// <summary>
    /// Reads of a singleton that came back null, once it may be read, before it is given up. At the 3 s settings tick
    /// this is two minutes. A getter whose mod catches its own exception returns null for ever, and is read for ever.
    /// </summary>
    public const int MaxNullReads = 40;

    public static bool IsEvidenceByItself(string memberName) => EvidenceByThemselves.Contains(memberName, StringComparer.Ordinal);

    public static bool GiveUp(int nullReads) => nullReads >= MaxNullReads;
}
