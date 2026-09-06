using System.Text.Json;
using ModderLords.Core.Profiles;
using Xunit;

namespace ModderLords.Core.Tests;

public class UiStateTests
{
    // A 1920x1080 primary with nothing to its left or above it: the ordinary single-monitor case.
    private const double L = 0, T = 0, W = 1920, H = 1080;
    private static UiState At(double left, double top, double w = 1280, double h = 820) =>
        new() { WindowLeft = left, WindowTop = top, WindowWidth = w, WindowHeight = h };

    [Fact]
    public void Geometry_on_screen_is_restored()
        => Assert.True(At(100, 100).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Geometry_from_an_unplugged_second_monitor_is_refused()
        => Assert.False(At(2600, 300).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Geometry_above_the_desktop_is_refused()
        => Assert.False(At(100, -400).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Geometry_below_the_desktop_is_refused()
        => Assert.False(At(100, 1200).GeometryFitsIn(L, T, W, H, 960, 600));

    /// <summary>A window mostly off the left edge is still fine as long as enough of it is grabbable.</summary>
    [Fact]
    public void Partly_offscreen_but_grabbable_is_restored()
        => Assert.True(At(-1100, 50).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Barely_visible_sliver_is_refused()
        => Assert.False(At(-1220, 50).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Geometry_smaller_than_the_window_minimum_is_refused()
        => Assert.False(At(100, 100, w: 400, h: 300).GeometryFitsIn(L, T, W, H, 960, 600));

    [Fact]
    public void Incomplete_geometry_is_refused()
        => Assert.False(new UiState { WindowLeft = 100, WindowTop = 100 }.GeometryFitsIn(L, T, W, H, 960, 600));

    /// <summary>A monitor placed to the LEFT of the primary gives negative virtual-screen coordinates.</summary>
    [Fact]
    public void Left_hand_monitor_coordinates_are_restored()
        => Assert.True(At(-1800, 100).GeometryFitsIn(-1920, 0, 3840, 1080, 960, 600));

    [Fact]
    public void Mode_round_trips_by_name()
    {
        var json = JsonSerializer.Serialize(new UiState { Mode = AppMode.Host },
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Contains("\"Host\"", json);
    }

    /// <summary>A null Mode is what makes the first-run dialog appear, so it must survive a round trip as null
    /// rather than defaulting to Player.</summary>
    [Fact]
    public void Missing_mode_stays_null()
    {
        var s = JsonSerializer.Deserialize<UiState>("{}");
        Assert.NotNull(s);
        Assert.Null(s!.Mode);
    }

    [Fact]
    public void Corrupt_state_file_loads_as_defaults_rather_than_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-uistate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui-state.json");
            File.WriteAllText(path, "{ this is not json");
            var loaded = UiStateStore.LoadFrom(path);
            Assert.Null(loaded.Mode);           // so the first-run dialog asks again
            Assert.Null(loaded.WindowLeft);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void State_round_trips_through_a_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-uistate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui-state.json");
            UiStateStore.SaveTo(path, new UiState
            {
                Mode = AppMode.Host, Theme = "Dark", SelectedTab = "Share",
                WindowLeft = 10, WindowTop = 20, WindowWidth = 1000, WindowHeight = 700, WindowMaximized = true,
            });
            var back = UiStateStore.LoadFrom(path);
            Assert.Equal(AppMode.Host, back.Mode);
            Assert.Equal("Dark", back.Theme);
            Assert.Equal("Share", back.SelectedTab);
            Assert.Equal(10, back.WindowLeft);
            Assert.True(back.WindowMaximized);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Loading_a_file_that_is_not_there_is_a_first_run()
        => Assert.Null(UiStateStore.LoadFrom(Path.Combine(Path.GetTempPath(), "ml-absent-" + Guid.NewGuid().ToString("N"), "ui-state.json")).Mode);
}

public class PlayerManifestTests
{
    private static readonly ModderLords.Core.Export.ClientManifest.Entry[] Two =
    [
        new("Bannerlord.Harmony", "v2.4.2", null),
        new("ModularSmithing2", "v0.9.30", "https://example/1"),
    ];

    /// <summary>Player mode is a mod loader: no Coop entry, and no instruction to disable DLC.</summary>
    [Fact]
    public void Player_text_has_no_coop_vocabulary()
    {
        var text = ModderLords.Core.Export.ClientManifest.ToPlayerText(Two);
        Assert.DoesNotContain("Coop", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DLC", text);
        Assert.Contains("Bannerlord.Harmony", text);
        Assert.Contains("ModularSmithing2", text);
        Assert.Contains("https://example/1", text);
    }

    /// <summary>The host text is unchanged: it still leads with Coop, which is what a joining player needs.</summary>
    [Fact]
    public void Host_text_still_leads_with_coop()
    {
        var text = ModderLords.Core.Export.ClientManifest.ToText(Two, "CoopNightly", "v0.1.4");
        Assert.Contains("CoopNightly", text);
        Assert.Contains("DLC", text);
    }
}
