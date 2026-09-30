using System;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Refreshes the open screen's view model so Bellum's panels re-read state that arrived from the server. The kingdom
/// screen exposes its KingdomManagementVM as DataSource; its RefreshValues cascades into the Policies tab, whose Bellum
/// mixin refreshes the succession panel. Reflection keeps this free of a SandBox.GauntletUI reference.
/// </summary>
internal static class BellumScreenRefresh
{
    public static void RefreshOpenScreen()
    {
        try
        {
            var screen = ScreenManager.TopScreen;
            if (screen == null) return;
            var type = screen.GetType();
            var vm = type.GetProperty("DataSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(screen) as ViewModel
                ?? type.GetField("_dataSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(screen) as ViewModel;
            vm?.RefreshValues();
        }
        catch (Exception ex) { ModderLords.CompatSync.Log.Warn("Bellum screen refresh failed: " + ex.GetBaseException().Message); }
    }
}
