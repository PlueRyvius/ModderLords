using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.Compat;

/// <summary>
/// The guards themselves. Each targets a TaleWorlds entry point that only makes sense with a screen attached and
/// either answers it (inquiries: pick the affirmative/first option so mod logic waiting on a callback continues) or
/// swallows it (screens). Every target is resolved by name and skipped when absent, so a game update degrades to
/// "guard not installed" instead of a crash. Each guard logs the first hit per calling assembly so the console shows
/// which mod tried to open UI on the server.
/// </summary>
internal static class Guards
{
    private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.Ordinal);

    public static string InstallAll(Harmony harmony)
    {
        var ok = new List<string>();
        var missing = new List<string>();

        void Try(string label, Func<bool> install)
        {
            try { if (install()) ok.Add(label); else missing.Add(label); }
            catch (Exception ex) { missing.Add(label + " (" + ex.GetBaseException().Message + ")"); }
        }

        // Inquiries that expect an answer: answer them.
        Try("MultiSelectionInquiry", () => Prefix(harmony, "TaleWorlds.Core.MBInformationManager", "ShowMultiSelectionInquiry", nameof(MultiSelectionInquiryPrefix)));
        Try("TextInquiry", () => Prefix(harmony, "TaleWorlds.Library.InformationManager", "ShowTextInquiry", nameof(TextInquiryPrefix)));
        // Plain inquiries are auto-accepted by the official server core already; install ours only if that patch is absent.
        Try("Inquiry", () => IsAlreadyPatched("TaleWorlds.Library.InformationManager", "ShowInquiry") || Prefix(harmony, "TaleWorlds.Library.InformationManager", "ShowInquiry", nameof(InquiryPrefix)));

        // Screens: there is no screen stack to push onto.
        foreach (var m in new[] { "PushScreen", "CleanAndPushScreen", "ReplaceTopScreen", "PopScreen", "CleanScreens" })
            Try("ScreenManager." + m, () => Prefix(harmony, "TaleWorlds.ScreenSystem.ScreenManager", m, nameof(SkipWithLogPrefix)));

        // Tooltips / system notifications route to UI event sinks; harmless when nobody listens, but mods sometimes
        // pass objects the server never builds (null visuals). Swallow them.
        Try("ShowTooltip", () => Prefix(harmony, "TaleWorlds.Library.InformationManager", "ShowTooltip", nameof(SkipWithLogPrefix)));

        return $"{ok.Count} active [{string.Join(", ", ok)}]" + (missing.Count > 0 ? $"; not installed [{string.Join(", ", missing)}]" : "");
    }

    // ---- patches -------------------------------------------------------------------------------------

    /// <summary>Answers a multi-selection inquiry with its first option (or negative when there is none).</summary>
    public static bool MultiSelectionInquiryPrefix(object data)
    {
        Announce("ShowMultiSelectionInquiry");
        try
        {
            var options = Traverse.Create(data).Property("InquiryElements").GetValue() as System.Collections.IList;
            var affirmative = Traverse.Create(data).Property("AffirmativeAction").GetValue() as Delegate;
            var negative = Traverse.Create(data).Property("NegativeAction").GetValue() as Delegate;
            if (options != null && options.Count > 0 && affirmative != null)
            {
                var chosen = new List<object> { options[0] };
                affirmative.DynamicInvoke(chosen);
            }
            else negative?.DynamicInvoke();
        }
        catch (Exception ex) { Log.Error("multi-selection inquiry auto-answer failed: " + ex.GetBaseException().Message); }
        return false;
    }

    /// <summary>Answers a text inquiry with its default text through the affirmative action.</summary>
    public static bool TextInquiryPrefix(object textData)
    {
        Announce("ShowTextInquiry");
        try
        {
            var affirmative = Traverse.Create(textData).Property("AffirmativeAction").GetValue() as Delegate;
            var defaultText = Traverse.Create(textData).Property("DefaultInputText").GetValue() as string ?? "";
            var negative = Traverse.Create(textData).Property("NegativeAction").GetValue() as Delegate;
            if (affirmative != null) affirmative.DynamicInvoke(defaultText); else negative?.DynamicInvoke();
        }
        catch (Exception ex) { Log.Error("text inquiry auto-answer failed: " + ex.GetBaseException().Message); }
        return false;
    }

    /// <summary>Accepts a yes/no inquiry (used only when the official core has not patched it already).</summary>
    public static bool InquiryPrefix(object data)
    {
        Announce("ShowInquiry");
        try
        {
            var affirmative = Traverse.Create(data).Property("AffirmativeAction").GetValue() as Delegate;
            affirmative?.DynamicInvoke();
        }
        catch (Exception ex) { Log.Error("inquiry auto-accept failed: " + ex.GetBaseException().Message); }
        return false;
    }

    public static bool SkipWithLogPrefix(MethodBase __originalMethod)
    {
        Announce(__originalMethod.DeclaringType?.Name + "." + __originalMethod.Name);
        return false;
    }

    // ---- plumbing ----------------------------------------------------------------------------------

    private static bool Prefix(Harmony harmony, string typeName, string methodName, string prefixName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null) return false;
        var target = AccessTools.Method(type, methodName);
        if (target == null) return false;
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(Guards), prefixName));
        return true;
    }

    private static bool IsAlreadyPatched(string typeName, string methodName)
    {
        var type = AccessTools.TypeByName(typeName);
        var target = type == null ? null : AccessTools.Method(type, methodName);
        if (target == null) return false;
        var info = Harmony.GetPatchInfo(target);
        return info != null && info.Prefixes.Count > 0;
    }

    /// <summary>Logs the first time each (entry point, calling mod assembly) pair is hit.</summary>
    private static void Announce(string entryPoint)
    {
        string caller = "?";
        try
        {
            var frames = new StackTrace(2, false).GetFrames();
            if (frames != null)
                foreach (var f in frames)
                {
                    var asm = f.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
                    if (asm == null || asm.StartsWith("TaleWorlds", StringComparison.Ordinal) || asm.StartsWith("0Harmony", StringComparison.Ordinal)
                        || asm == "DedicatedServer.ModderLordsCompat" || asm.StartsWith("System", StringComparison.Ordinal) || asm == "mscorlib") continue;
                    caller = asm; break;
                }
        }
        catch { }
        var key = entryPoint + "|" + caller;
        lock (Announced)
        {
            if (!Announced.Add(key)) return;
        }
        Log.Info($"guard: {entryPoint} called by {caller}; handled headlessly (further hits from this mod are not logged)");
    }
}
