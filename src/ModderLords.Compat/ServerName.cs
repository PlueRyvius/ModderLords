using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.Compat;

/// <summary>
/// The name players see for this server in Coop's server browser (the Host column), from the Server tab's Server
/// name (MODDERLORDS_SERVER_NAME, set by the launcher).
/// <para>
/// Coop has no setting for it: it advertises the Steam display name of the account logged in on the server machine,
/// as the lobby's "coop_owner_name" (Coop.Steam.SteamLobbyAdvertiser, through SteamLobbyApi.SetLobbyData). This
/// swaps that one value as it is written. Nothing else Coop sends changes, and players joining by address see no
/// name either way. Off when no name is set, or when Coop's Steam code is not found (Coop changed): then the Steam
/// name shows, as before.
/// </para>
/// </summary>
internal static class ServerName
{
    internal const string EnvironmentVariable = "MODDERLORDS_SERVER_NAME";
    internal const string LobbyKey = "coop_owner_name";
    internal const int MaxLength = 64;
    private const string LobbyApi = "Coop.Steam.SteamLobbyApi";

    private static string? _name;
    private static Harmony? _harmony;
    private static bool _patched;

    internal static void Install(Harmony harmony)
    {
        var name = Clean(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (name == null) return;
        _name = name;
        _harmony = harmony;
        if (TryPatch()) return;
        // Coop's Steam assembly may load after this module; patch it when it does.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        Log.Info($"server name: waiting for Coop's Steam code to advertise \"{name}\"");
    }

    /// <summary>The name as advertised: trimmed, control characters removed, at most <see cref="MaxLength"/> characters; null when empty.</summary>
    internal static string? Clean(string? raw)
    {
        if (raw == null) return null;
        var name = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxLength) name = name.Substring(0, MaxLength).TrimEnd();
        return name.Length == 0 ? null : name;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (_patched || args.LoadedAssembly.GetName().Name != "Coop.Steam") return;
        if (TryPatch()) AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    private static bool TryPatch()
    {
        if (_patched) return true;
        var type = AccessTools.TypeByName(LobbyApi);
        if (type == null) return false;
        var set = AccessTools.Method(type, "SetLobbyData", new[] { typeof(ulong), typeof(string), typeof(string) });
        if (set == null)
        {
            Log.Info($"server name: {LobbyApi}.SetLobbyData not found (Coop changed?); players see the Steam name");
            _patched = true;
            return true;
        }
        _harmony!.Patch(set, prefix: new HarmonyMethod(typeof(ServerName), nameof(SetLobbyDataPrefix)));
        _patched = true;
        Log.Info($"server name: advertised as \"{_name}\"");
        return true;
    }

    private static void SetLobbyDataPrefix(string key, ref string value)
    {
        if (_name != null && key == LobbyKey) value = _name;
    }
}
