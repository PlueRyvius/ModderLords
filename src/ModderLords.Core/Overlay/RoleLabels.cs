namespace ModderLords.Core.Overlay;

/// <summary>
/// What the Mods tab's Role cell offers. A way of SHOWING <see cref="ServerRole"/> and the separate Server-only
/// logic tick together, never something that is stored: profiles, shared mod lists, the compat database, the CLI
/// and the launch log all keep the role and the tick.
/// </summary>
public enum RoleChoice
{
    /// <summary><see cref="ServerRole.Run"/>: the mod's code loads on the server and on players' games.</summary>
    ServerAndClient,
    /// <summary><see cref="ServerRole.DependencyOnly"/>: listed on the server for the handshake, code on players' games only.</summary>
    ClientOnly,
    /// <summary><see cref="ServerRole.Run"/> with Server-only logic ticked. Experimental compatibility only.</summary>
    ServerOnly,
    /// <summary><see cref="ServerRole.AsShipped"/>: the mod's own manifest decides what the server loads.</summary>
    ModDecides,
}

/// <summary>
/// The one place the plain-language names of the roles are written. The Role column showed the enum names (Run,
/// DependencyOnly, AsShipped) and hosts did not know what they meant, so the Mods tab now shows these instead - and
/// every other line a host reads (tooltips, the Record dialog, launch notes) takes its wording from here, so a note
/// can never name a choice the drop-down does not have.
/// </summary>
public static class RoleLabels
{
    public const string ServerAndClient = "Server + Client";
    public const string ClientOnly = "Client only";
    public const string ServerOnly = "Server only";
    public const string ModDecides = "Mod decides";

    public static string For(RoleChoice choice) => choice switch
    {
        RoleChoice.ServerAndClient => ServerAndClient,
        RoleChoice.ClientOnly => ClientOnly,
        RoleChoice.ServerOnly => ServerOnly,
        _ => ModDecides,
    };

    /// <summary>The label for a stored role on its own, with nothing known about the Server-only logic tick.</summary>
    public static string For(ServerRole role) => For(Choice(role, serverOnlyLogic: false, experimental: false));

    /// <summary>
    /// What the Role cell shows for a stored role and tick. With Experimental compatibility off a launch ignores the
    /// tick, so a ticked Run mod is shown as what the launch will do with it: Server + Client.
    /// </summary>
    public static RoleChoice Choice(ServerRole role, bool serverOnlyLogic, bool experimental) => role switch
    {
        ServerRole.Run => serverOnlyLogic && experimental ? RoleChoice.ServerOnly : RoleChoice.ServerAndClient,
        ServerRole.DependencyOnly => RoleChoice.ClientOnly,
        _ => RoleChoice.ModDecides,
    };

    /// <summary>The stored role a choice stands for. The tick is the caller's business: see ModRow.RoleChoice.</summary>
    public static ServerRole Role(RoleChoice choice) => choice switch
    {
        RoleChoice.ClientOnly => ServerRole.DependencyOnly,
        RoleChoice.ModDecides => ServerRole.AsShipped,
        _ => ServerRole.Run,
    };

    /// <summary>
    /// The aside a log line puts after a role it names: <c>("Client only" on the Mods tab)</c>. The stored name stays
    /// in the line - people search logs for it, and it is what the CLI and the profile file say - and this tells a
    /// host which entry of the drop-down it is.
    /// </summary>
    public static string OnModsTab(ServerRole role) => $"(\"{For(role)}\" on the Mods tab)";

    /// <summary>The stored name followed by <see cref="OnModsTab"/>: <c>DependencyOnly ("Client only" on the Mods tab)</c>.</summary>
    public static string InLog(ServerRole role) => $"{role} {OnModsTab(role)}";

    /// <summary>
    /// A stored pair of defaults (a compat record's, or a shared list's entry) in the Mods tab's words. Not tied to
    /// the Experimental switch: this describes what is stored, so Run with the tick reads "Server only" even where
    /// the switch is off. A tick on any other role does not change the role, so it is spelled out beside it.
    /// </summary>
    public static string Describe(ServerRole? role, bool? serverOnlyLogic, string unset)
    {
        if (role == ServerRole.Run && serverOnlyLogic == true) return ServerOnly;
        return (role is { } r ? For(r) : unset)
               + ", server-only logic " + (serverOnlyLogic is true ? "on" : serverOnlyLogic is false ? "off" : unset);
    }
}
