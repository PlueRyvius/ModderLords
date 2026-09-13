using System.IO;
using System.Windows;
using ModderLords.Core.Profiles;

namespace ModderLords.App;

/// <summary>Which theme dictionary is loaded. Persisted by name so the stored value survives reordering.</summary>
public enum AppTheme { Light, Dark }

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Log instead of dying: an exception in a UI handler must never take the server console down with it.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(args.Exception.Message, "ModderLords: error (logged)", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        // Upgraders from the ModularCoop-named builds keep their profiles; the old folder is copied, not moved.
        try { DataDirMigration.RunIfNeeded(); }
        catch (Exception ex) { LogCrash(ex); }

        // After an in-app update the previous exe was renamed to .old because it was still running; it has exited now.
        var i = Array.IndexOf(e.Args, "--updated-from");
        if (i >= 0 && i + 1 < e.Args.Length) UpdatedFrom = e.Args[i + 1];
        try { ModderLords.Core.Updates.UpdateInstaller.ForThisApp().CleanUpAfterUpdate(); }
        catch (Exception ex) { LogCrash(ex); }
        // Right after an update the old process is usually still exiting, so its renamed exe is still locked and the
        // delete above fails (seen in the end-to-end test). Keep trying briefly in the background rather than leaving
        // ModderLords.exe.old beside the new one until the next start.
        if (UpdatedFrom is not null)
            _ = Task.Run(async () =>
            {
                var installer = ModderLords.Core.Updates.UpdateInstaller.ForThisApp();
                for (var attempt = 0; attempt < 15; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    try
                    {
                        installer.CleanUpAfterUpdate();
                        if (!Directory.EnumerateFiles(installer.InstallDir, ModderLords.Core.Updates.UpdateInstaller.ExeName + ".old*").Any()) return;
                    }
                    catch (Exception) { }
                }
            });
    }

    /// <summary>The version this copy replaced, when it was just started by the updater; null on a normal start.</summary>
    public static string? UpdatedFrom { get; private set; }

    /// <summary>
    /// Swaps the theme dictionary in slot 0 of the app's merged dictionaries (see App.xaml). Shared.xaml sits
    /// after it and looks its colours up with DynamicResource, so every open window repaints without a restart.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var merged = Current.Resources.MergedDictionaries;
        var uri = new Uri($"Themes/{(theme == AppTheme.Dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(ProfileStore.RootDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"), $"{DateTime.Now:O} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
