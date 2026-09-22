using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Profiles;

/// <summary>
/// Which half of the app the user wants. Player mode is a mod loader and nothing else; Host mode adds the
/// dedicated-server tabs. The split is presentation only — <c>ModderLords.Core</c> cannot reach the server
/// code at all (the Phase 2 assembly split enforces that), so this decides what is *shown*, never what is
/// *reachable*.
/// </summary>
public enum AppMode { Player, Host }

/// <summary>
/// Everything the window should remember between runs. Nothing was remembered before this existed, so theme,
/// geometry and the selected tab live here too rather than each growing its own file.
/// </summary>
public sealed class UiState
{
    /// <summary>Null until the first-run dialog has been answered; that null is what triggers the dialog.</summary>
    public AppMode? Mode { get; set; }

    /// <summary>"Light" or "Dark". A name, not an index, so reordering the enum cannot repaint someone's app.</summary>
    public string? Theme { get; set; }

    /// <summary>Header of the tab that was selected. By header rather than index, because the visible tabs
    /// differ between the two modes and an index would land somewhere arbitrary after a mode switch.</summary>
    public string? SelectedTab { get; set; }

    /// <summary>Whether to ask GitHub for a newer release on startup (at most once a day). Downloading always needs a click.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the startup check last ran, so it runs at most once a day however often the app is opened.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>A release the user chose "Skip this version" on. A later release is offered again.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Experimental compatibility (Server tab, Advanced). Off by default; see MainViewModel.ExperimentalCompat.</summary>
    public bool ExperimentalCompat { get; set; }

    /// <summary>Subscribe to a shared list's missing Workshop mods through Steam when importing it. Opt-in: it changes the Steam account.</summary>
    public bool AutoSubscribeWorkshop { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// True when the saved geometry is safe to apply: it is complete, big enough to be usable, and enough of its
    /// title bar overlaps the desktop to be draggable. A monitor that has been unplugged since the last run is the
    /// case this exists for — restoring a window onto coordinates that no longer exist hides the app completely.
    /// </summary>
    public bool GeometryFitsIn(double screenLeft, double screenTop, double screenWidth, double screenHeight,
                               double minWidth, double minHeight)
    {
        if (WindowLeft is not { } l || WindowTop is not { } t || WindowWidth is not { } w || WindowHeight is not { } h)
            return false;
        if (double.IsNaN(l) || double.IsNaN(t) || double.IsNaN(w) || double.IsNaN(h)) return false;
        if (w < minWidth || h < minHeight) return false;

        // The grab strip: enough of the top edge on screen that the window can be dragged back into view.
        const double MinVisible = 120;
        var right = screenLeft + screenWidth;
        var bottom = screenTop + screenHeight;
        if (t < screenTop || t > bottom - 32) return false;
        if (l + w < screenLeft + MinVisible) return false;
        if (l > right - MinVisible) return false;
        return true;
    }
}

public static class UiStateStore
{
    public static string Path => System.IO.Path.Combine(ProfileStore.RootDir, "ui-state.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Never throws: a corrupt or unreadable state file must cost the user their window position, not
    /// their launcher. A fresh <see cref="UiState"/> has a null Mode, so the first-run dialog asks again.</summary>
    public static UiState Load() => LoadFrom(Path);

    public static UiState LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UiState();
            return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Json) ?? new UiState();
        }
        catch { return new UiState(); }
    }

    /// <summary>Also never throws. Written through a temp file so a crash mid-save cannot leave a half file.</summary>
    public static void Save(UiState state) => SaveTo(Path, state);

    public static void SaveTo(string path, UiState state)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
