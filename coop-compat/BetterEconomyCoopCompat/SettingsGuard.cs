using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BetterEconomyCoopCompat;

/// <summary>
/// Same settings everywhere, and nothing that needs a keyboard on the server .
///
/// Living Economy's 583 tuning values (BetterEconomySettings, public static fields) must be the same on the server and
/// every player's PC: copy the host's settings files to the players (ModderLords' Settings sync does it for you). Two
/// things would undo that or break the server, and this component stops them:
/// - Players: the mod's Ctrl+Shift+M reloads its XML files over whatever the host sent. In a co-op session the reload
///   is refused with a line saying why (it still works alone, and at start-up before any session).
/// - Server: the mod polls the keyboard every frame for its ledger hotkey; a dedicated server has no keyboard, so
///   that tick is skipped there. The mod's own culture conversion (it writes Settlement.Culture directly, off by
///   default) is held off on the server until co-op replication of that write has been tested; a host who turned it
///   on is told so in the log.
/// </summary>
internal sealed class SettingsGuard : IComponent
{
    private const string Owner = "modderlords.bettereconomy.coopcompat.SettingsGuard";

    private readonly List<MethodInfo> _reloads = new List<MethodInfo>();
    private MethodInfo? _hotkeyTick;
    private FieldInfo? _cultureSwitch;
    private PropertyInfo? _runtimeInstance;
    private static DateTime _lastRefusal = DateTime.MinValue;

    public string Id => "settings-guard";

    public string? SkipReason(Context context)
    {
        _reloads.Clear();
        if (context.IsServer)
        {
            _hotkeyTick = context.Method("BetterEconomy.UI.HotkeyHandler", "Tick", 0);
            var runtime = context.Type("BetterEconomy.Config.RuntimeSettings");
            _runtimeInstance = runtime?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _cultureSwitch = runtime?.GetField("EnableVanillaSettlementCultureConversion", BindingFlags.Public | BindingFlags.Instance);
            return _hotkeyTick == null && _cultureSwitch == null ? "Living Economy changed; HotkeyHandler.Tick and RuntimeSettings not found" : null;
        }
        foreach (var type in new[] { "BetterEconomy.Config.SettingsLoader", "BetterEconomy.Config.RegionalProfiles", "BetterEconomy.Config.ClassConsumptionProfiles" })
            if (context.Method(type, "LoadOrLog", 0) is { } m) _reloads.Add(m);
        _hotkeyTick = context.Method("BetterEconomy.UI.HotkeyHandler", "Tick", 0);
        return _reloads.Count == 0 && _hotkeyTick == null ? "Living Economy changed; its settings loaders and ledger hotkey were not found" : null;
    }

    public string Install(Context context)
    {
        var h = new Harmony(Owner);
        if (!context.IsServer)
        {
            foreach (var m in _reloads) h.Patch(m, prefix: new HarmonyMethod(typeof(SettingsGuard), nameof(ReloadPrefix)));
            if (_hotkeyTick != null) h.Patch(_hotkeyTick, prefix: new HarmonyMethod(typeof(SettingsGuard), nameof(LedgerHotkeyPrefix)));
            return "players cannot reload Living Economy's XML over the host's settings during co-op; its ledger hotkey yields to Coop chat";
        }
        var parts = new List<string>();
        if (_hotkeyTick != null)
        {
            h.Patch(_hotkeyTick, prefix: new HarmonyMethod(typeof(SettingsGuard), nameof(Skip)));
            parts.Add("hotkey polling skipped");
        }
        if (_cultureSwitch != null && _runtimeInstance?.GetValue(null) is { } runtime)
        {
            if (_cultureSwitch.GetValue(runtime) is true)
                Log.Warn("the host turned on Living Economy's settlement culture conversion; it is held off in co-op for now (Settlement.Culture changes are not yet confirmed to reach players)");
            _cultureSwitch.SetValue(runtime, false);   // in memory only; the host's file is not rewritten
            parts.Add("settlement culture conversion held off");
        }
        return string.Join("; ", parts);
    }

    private static bool Skip() => false;

    /// <summary>Keep Living Economy's M hotkey from opening its ledger on top of Coop's active chat input.</summary>
    private static bool LedgerHotkeyPrefix() => !Layer.IsCoopClient || !IsCoopChatOpen();

    private static Type? _chatType;
    private static MethodInfo? _resolve;

    private static bool IsCoopChatOpen()
    {
        try
        {
            _chatType ??= AccessTools.TypeByName("GameInterface.Services.Chat.IChatService");
            _resolve ??= AccessTools.TypeByName("GameInterface.ContainerProvider")?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "TryResolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (_chatType == null || _resolve == null) return false;
            var args = new object?[] { null };
            if (!(_resolve.MakeGenericMethod(_chatType).Invoke(null, args) is true) || args[0] == null) return false;
            return _chatType.GetProperty("IsTyping")?.GetValue(args[0]) is true;
        }
        catch { return false; }
    }

    private static bool ReloadPrefix()
    {
        if (!Layer.IsCoopClient) return true;
        if (DateTime.UtcNow - _lastRefusal > TimeSpan.FromSeconds(2))
        {
            _lastRefusal = DateTime.UtcNow;
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(new TextObject(
                    "{=ml_le_no_reload}[LivingEconomy] In co-op the host's settings are used; reloading your own XML is turned off.").ToString(), Colors.Yellow));
            }
            catch { }
        }
        return false;
    }
}
