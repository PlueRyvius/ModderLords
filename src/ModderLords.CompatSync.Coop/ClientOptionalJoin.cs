using System;
using System.Collections.Generic;
using System.Linq;
using GameInterface.Services.Modules;
using HarmonyLib;
using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server: leaves the host's "Client optional" mods out of Coop's join check.
///
/// <para>Coop's <c>ModuleValidator</c> wants a player's community mods to match the server's exactly in both
/// directions, so a player running a mod the server does not list is refused with "Server does not support module".
/// A Client optional mod is not on the server by design (an overlay, a camera, a UI change: something only that player
/// sees), and each player decides whether to run it. The launcher passes those ids in
/// <c>MODDERLORDS_CLIENT_OPTIONAL_MODS</c>; here they are dropped from the player's list before the check runs. Every
/// other mod is still matched id for id and version for version, and the check itself is untouched.</para>
/// </summary>
public static class ClientOptionalJoin
{
    /// <summary>The launcher's name for it: this module cannot reference ModderLords.Core, where it is declared.</summary>
    public const string Variable = "MODDERLORDS_CLIENT_OPTIONAL_MODS";

    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.ClientOptionalJoin");
    private static bool _installTried;
    private static HashSet<string> _optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Patches the join check once when the launcher named any optional mods. Safe to call every tick.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried) return;
        _installTried = true;
        if (!Operations.OperationProcessSide.IsServer) return;
        _optional = Parse(Environment.GetEnvironmentVariable(Variable));
        if (_optional.Count == 0) return;
        try
        {
            var validator = AccessTools.TypeByName("GameInterface.Services.Modules.Validators.ModuleValidator");
            var validate = validator == null ? null : AccessTools.Method(validator, "Validate");
            if (validate == null)
            {
                Log.Warn("client optional mods: Coop's join check has changed shape, so a player running "
                         + string.Join(", ", _optional) + " will be refused");
                return;
            }
            Harmony.Patch(validate, prefix: new HarmonyMethod(typeof(ClientOptionalJoin), nameof(Prefix)));
            Log.Info("client optional mods: players may join with or without " + string.Join(", ", _optional.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { Log.Warn("client optional mods: not installed: " + ex.GetBaseException().Message); }
    }

    internal static HashSet<string> Parse(string? value)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return ids;
        foreach (var id in value!.Split('|'))
            if (id.Trim().Length > 0) ids.Add(id.Trim());
        return ids;
    }

    private static void Prefix(ref IEnumerable<ModuleInfo> clientModules)
    {
        if (clientModules == null || _optional.Count == 0) return;
        var all = clientModules.ToList();
        var kept = all.Where(m => m.Id == null || !_optional.Contains(m.Id)).ToList();
        if (kept.Count == all.Count) return;
        foreach (var m in all)
            if (m.Id != null && _optional.Contains(m.Id) && Said.Add(m.Id))
                Log.Info("client optional mods: a joining player runs " + m.Id + "; left out of the join check (said once per mod)");
        clientModules = kept;
    }
}
