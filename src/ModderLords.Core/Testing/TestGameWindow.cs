using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModderLords.Core.Testing;

/// <summary>
/// Keeps the game's window out of the player's way during a test run. The steps run on the game's own tick, not on
/// input, so the game never needs the focus; it takes it anyway when its window opens. Each time it does, this gives
/// the focus back to the window that had it and puts the game behind it, or minimizes the game when asked.
/// <para>
/// The player can still look: a click on the game's window leaves it in front (the cursor is over it and the input
/// was just now), until another window takes the focus.
/// </para>
/// </summary>
public sealed class TestGameWindow
{
    private readonly Process _game;
    private readonly bool _minimized;
    private readonly Action<string> _say;
    private int _pushed;

    private TestGameWindow(Process game, bool minimized, Action<string> say)
    {
        _game = game;
        _minimized = minimized;
        _say = say;
    }

    /// <summary>
    /// Starts watching the game's window until the game exits or <paramref name="stop"/> is cancelled (the run is over;
    /// a game kept open is then the player's). Does nothing off Windows.
    /// </summary>
    public static Task Watch(Process game, bool minimized, Action<string> say, CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var watcher = new TestGameWindow(game, minimized, say);
        return Task.Run(() => watcher.Loop(stop), CancellationToken.None);
    }

    private async Task Loop(CancellationToken stop)
    {
        var previous = GetForegroundWindow();
        var gameWasInFront = false;
        var minimizedOnce = false;
        try
        {
            while (!stop.IsCancellationRequested && !_game.HasExited)
            {
                var front = GetForegroundWindow();
                var inFront = front != IntPtr.Zero && OwnerOf(front) == _game.Id;
                if (_minimized && !minimizedOnce && MainWindow() is { } main && IsWindowVisible(main) && !IsDialog(main))
                {
                    // Minimized from the moment it shows, in front or not.
                    ShowWindow(main, SW_MINIMIZE);
                    minimizedOnce = true;
                    _say($"the game window was minimized; it keeps running (restore it from the taskbar to watch)");
                }
                else if (inFront && !gameWasInFront)
                {
                    if (IsDialog(front))
                    {
                        // A message box waits for a person; hiding it would only make it harder to answer.
                        _say("the game is showing a message box and waits for someone to answer it; it is left in front");
                    }
                    else if (PlayerPicked(front)) { /* the player clicked it: theirs until they click away */ }
                    else PushBack(front, previous);
                }
                else if (!inFront && front != IntPtr.Zero) previous = front;
                gameWasInFront = inFront && GetForegroundWindow() == front;
                await Task.Delay(250, stop);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // the process is gone
        if (_pushed > 1) _say($"the game took the focus {_pushed} times during the run and was put back each time");
    }

    private void PushBack(IntPtr game, IntPtr previous)
    {
        _pushed++;
        if (_minimized)
        {
            ShowWindow(game, SW_MINIMIZE);
            if (_pushed == 1) _say("the game came to the front; it was minimized again");
            return;
        }
        // Give the focus back first; Windows may refuse that to a process that is not in front.
        if (previous != IntPtr.Zero && IsWindow(previous) && OwnerOf(previous) != _game.Id && SetForegroundWindow(previous) && GetForegroundWindow() == previous)
            SetWindowPos(game, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else
        {
            // Minimizing hands the focus to the next window; showing it again without activating keeps it there.
            ShowWindow(game, SW_MINIMIZE);
            ShowWindow(game, SW_SHOWNOACTIVATE);
            SetWindowPos(game, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        if (_pushed == 1) _say("the game came to the front; it was put behind your other windows (click it to watch)");
    }

    /// <summary>The game is in front because the player clicked it: the cursor is over it and there was input just now.</summary>
    private static bool PlayerPicked(IntPtr window)
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return false;
        var idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return idle < 1000 && GetCursorPos(out var at) && GetWindowRect(window, out var r)
            && at.X >= r.Left && at.X < r.Right && at.Y >= r.Top && at.Y < r.Bottom;
    }

    /// <summary>A Win32 dialog, such as the engine's "Mod change detected" box: the class every MessageBox uses.</summary>
    private static bool IsDialog(IntPtr window)
    {
        var name = new System.Text.StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770";
    }

    private IntPtr? MainWindow()
    {
        _game.Refresh();
        var h = _game.MainWindowHandle;
        return h == IntPtr.Zero ? null : h;
    }

    private static int OwnerOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    private const int SW_MINIMIZE = 6, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}
