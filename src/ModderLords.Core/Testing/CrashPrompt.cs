using System.Runtime.InteropServices;
using System.Text;

namespace ModderLords.Core.Testing;

/// <summary>
/// Answers Bannerlord's crash prompt with No during a test run, so it does not sit on the desktop.
/// <para>
/// When the engine's watchdog catches a crash (the game's or the dedicated server's) it asks "The application faced a
/// problem. We need to collect necessary files to fix this problem. Would you like to proceed?" in a dialog titled
/// <c>*_*</c>. The dialog is not the game's window (Windows shows it for the watchdog), so closing the game's process
/// tree does not take it away, and a run used to leave it up until it ended. No keeps every log a run needs (the engine
/// logs are written either way and the run copies them); Yes would write the game's dump and hand files to the crash
/// uploader, which the maintainer chose not to do unattended (2026-11-15).
/// </para>
/// </summary>
public static class CrashPrompt
{
    /// <summary>The words that make a dialog the crash prompt; the title alone (<c>*_*</c>) is not specific enough.</summary>
    public const string Question = "collect necessary files";

    /// <summary>Whether a dialog's controls' texts are the watchdog's crash prompt: its question and a No button.</summary>
    public static bool IsCrashPrompt(IEnumerable<string> texts)
    {
        var all = texts.ToList();
        return all.Any(t => t.Contains(Question, StringComparison.OrdinalIgnoreCase)) && all.Any(IsNo);
    }

    /// <summary>A button caption meaning No ("No", "&amp;No").</summary>
    public static bool IsNo(string caption) => caption.Replace("&", "").Trim().Equals("No", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Presses No on every crash prompt on the desktop. Returns how many it answered (0 off Windows). Never throws.
    /// </summary>
    public static int AnswerNo()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var answered = 0;
        try
        {
            var dialogs = new List<IntPtr>();
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && ClassOf(window) == "#32770") dialogs.Add(window);
                return true;
            }, IntPtr.Zero);
            foreach (var dialog in dialogs)
            {
                var children = new List<(IntPtr Handle, string Class, string Text)>();
                EnumChildWindows(dialog, (child, _) => { children.Add((child, ClassOf(child), TextOf(child))); return true; }, IntPtr.Zero);
                if (!IsCrashPrompt(children.Select(c => c.Text))) continue;
                var no = children.FirstOrDefault(c => c.Class == "Button" && IsNo(c.Text));
                if (no.Handle == IntPtr.Zero) continue;
                SendMessage(no.Handle, BmClick, IntPtr.Zero, IntPtr.Zero);
                answered++;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { }
        return answered;
    }

    private const uint BmClick = 0x00F5;

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static string TextOf(IntPtr window)
    {
        var text = new StringBuilder(512);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    private delegate bool EnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
