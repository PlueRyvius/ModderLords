using System.Windows;
using ModderLords.App.Localization;
using ModderLords.Core.Profiles;
using ModderLords.Core.Updates;

namespace ModderLords.App;

/// <summary>
/// Downloads the GitHub releases a verified profile names (docs/PROFILE-MANAGER.md, decision 12), one after another,
/// with each one's progress. Closing it stops the download in progress; what finished stays installed.
/// </summary>
public partial class ModDownloadWindow : Window
{
    private readonly IReadOnlyList<VerifiedMod> _mods;
    private readonly string[] _lines;
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>True once at least one mod is installed, so the caller knows to look again.</summary>
    public bool AnyInstalled { get; private set; }

    public ModDownloadWindow(IReadOnlyList<VerifiedMod> mods)
    {
        InitializeComponent();
        _mods = mods;
        _lines = mods.Select(m => Line(m, Loc.T("waiting"))).ToArray();
        Render();
        Loaded += async (_, _) => await RunAsync();
    }

    private static string Line(VerifiedMod m, string state) => $"{m.Id,-28} {m.Version,-12} {state}";

    private void Render() => Items.ItemsSource = _lines.ToList();

    private async Task RunAsync()
    {
        Header.Text = Loc.F("Downloading {0} mods from GitHub…", _mods.Count);
        using var http = UpdateInstaller.CreateDownloadClient();
        var failed = 0;
        for (var i = 0; i < _mods.Count; i++)
        {
            var index = i;
            var mod = _mods[i];
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                _lines[index] = Line(mod, p.Total is { } t and > 0 ? $"{p.Done >> 20} / {t >> 20} MB" : $"{p.Done >> 20} MB");
                Render();
            });
            try
            {
                await DownloadedMods.InstallAsync(mod, http, progress, _cancel.Token);
                _lines[index] = Line(mod, Loc.T("installed"));
                AnyInstalled = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failed++;
                _lines[index] = Line(mod, Loc.T("failed: ") + ex.Message);
            }
            Render();
        }
        Header.Text = failed == 0 ? Loc.T("All done.") : Loc.T("Finished — some mods did not install.");
        CloseButton.Content = Loc.T("Close");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        base.OnClosed(e);
    }
}
