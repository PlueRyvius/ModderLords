using System.IO;
using System.Windows;
using ModularCoop.Core.Profiles;

namespace ModularCoop.App;

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
