using System.IO;
using System.Windows;
using ModularCoop.Core.Profiles;

namespace ModularCoop.App;

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
            MessageBox.Show(args.Exception.Message, "Modular Bannerlords Coop: error (logged)", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
    }

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
