using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// The standalone Bellum Civile Coop Compat module (<c>coop-compat/BellumCivileCoopCompat</c>), when the player has it.
/// It carries the same Bellum patches as this assembly, written to work without ModderLords, so when it is loaded this
/// assembly's own copies stay off and the two never patch Bellum twice. What only ModderLords can do is plugged into
/// its public <c>Hooks</c> class: telling a realm's players a treaty's terms over the notice channel, and the vote
/// deadline from <see cref="KingdomVoteTimeoutAi"/>.
/// </summary>
public static class StandaloneBellum
{
    private const string HooksType = "BellumCivileCoopCompat.Hooks";
    private const int HooksVersion = 1;

    private static bool _resolved, _wired, _compatible;
    private static Type? _hooks;
    private static MethodInfo? _onDeadline, _summary;

    /// <summary>True when the standalone module is loaded, in any version: this assembly's Bellum patches then stay off.</summary>
    public static bool Present
    {
        get
        {
            Resolve();
            return _hooks != null;
        }
    }

    /// <summary>Hands the module what only ModderLords has. Safe to call every tick.</summary>
    public static void EnsureWired()
    {
        if (_wired || !Present || !_compatible) return;
        _wired = true;
        try
        {
            AccessTools.Property(_hooks, "TellRealm")?.SetValue(null, new Action<Kingdom, string>((realm, text) => BellumTreatyVote.Tell(realm, text)), null);
            Log.Info("Bellum: the standalone Bellum Civile Coop Compat module is loaded; ModderLords' own Bellum patches stay off and its notices go through ModderLords");
        }
        catch (Exception ex) { Log.Warn("Bellum: could not wire the standalone module: " + ex.GetBaseException().Message); }
    }

    /// <summary>The vote round ran out. True when the vote is one of the module's treaty votes; see <see cref="BellumTreatyVote.OnDeadline"/>.</summary>
    public static bool OnDeadline(KingdomDecision decision, bool rulerVoted)
    {
        if (_onDeadline == null) return false;
        try { return _onDeadline.Invoke(null, new object[] { decision, rulerVoted }) is true; }
        catch (Exception ex) { Log.Warn("Bellum: the standalone module's vote deadline failed: " + ex.GetBaseException().Message); return false; }
    }

    public static string Summary()
    {
        if (_summary == null) return "";
        try { return _summary.Invoke(null, null) is string s && s.Length > 0 ? "; standalone Bellum" + s : ""; }
        catch { return ""; }
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        var hooks = AccessTools.TypeByName(HooksType);
        if (hooks == null) return;
        var version = AccessTools.Field(hooks, "Version")?.GetRawConstantValue();
        if (version is not int v || v != HooksVersion)
        {
            Log.Warn($"Bellum: the standalone Bellum Civile Coop Compat module speaks hooks version {version ?? "?"}, this build {HooksVersion}; "
                + "ModderLords' own Bellum patches stay off so Bellum is not patched twice, and its extras are not wired");
            _hooks = hooks;
            return;
        }
        _hooks = hooks;
        _compatible = true;
        _onDeadline = AccessTools.Method(hooks, "OnVoteDeadline", new[] { typeof(KingdomDecision), typeof(bool) });
        _summary = AccessTools.Method(hooks, "Summary", Type.EmptyTypes);
    }
}
