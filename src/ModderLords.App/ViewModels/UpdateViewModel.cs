using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Core.Profiles;
using ModderLords.Core.Updates;

namespace ModderLords.App.ViewModels;

/// <summary>
/// The update banner: notices a newer release, and installs it when - and only when - the user clicks. Nothing is
/// downloaded without that click, and the check itself never shows an error: offline is not the user's problem.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly UiState _ui;

    /// <summary>
    /// A dev build runs from bin\Debug. Swapping a release zip into that folder would overwrite the build output with
    /// an unrelated copy, so a dev build only ever points at the release page.
    /// </summary>
#if DEBUG
    private static readonly bool IsDevBuild = true;
#else
    private static readonly bool IsDevBuild = false;
#endif

    /// <summary>How long a check that found nothing is trusted. An update that WAS found is offered on every start.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromHours(6);

    public UpdateViewModel(MainViewModel main, UiState ui)
    {
        _main = main;
        _ui = ui;
    }

    public static Version Running => typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(BannerText))]
    private ReleaseInfo? _available;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";

    public bool HasUpdate => Available is not null;

    public string BannerText => Available is { } r
        ? $"ModderLords {UpdateChecker.Format(r.Version)} is available — you have {UpdateChecker.Format(Running)}."
        : "";

    /// <summary>Called once the window has drawn. Silent whatever happens.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_ui.CheckForUpdates) return;
        if (_ui.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < QuietPeriod) return;
        await RunCheckAsync(userAsked: false);
    }

    [RelayCommand]
    private Task CheckNow() => RunCheckAsync(userAsked: true);

    private async Task RunCheckAsync(bool userAsked)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            using var http = UpdateChecker.CreateCheckClient();
            // A check the user asked for also offers a version they once skipped: asking is changing their mind.
            var result = await UpdateChecker.CheckAsync(http, Running, userAsked ? null : _ui.SkippedVersion);
            if (result.Reached)
            {
                Available = result.Release;
                // Remember only "nothing new", so a found update keeps being offered until it is dealt with.
                if (result.Release is null)
                {
                    _ui.LastUpdateCheckUtc = DateTime.UtcNow;
                    UiStateStore.Save(_ui);
                }
            }
            if (userAsked)
                _main.Status = !result.Reached ? "Could not reach GitHub to check for updates. Try again later."
                    : result.Release is { } r ? $"ModderLords {UpdateChecker.Format(r.Version)} is available."
                    : $"ModderLords {UpdateChecker.Format(Running)} is the latest version.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ShowNotes()
    {
        if (Available is not { } r) return;
        var notes = string.IsNullOrWhiteSpace(r.Notes) ? "(No release notes.)" : r.Notes.Trim();
        if (notes.Length > 3000) notes = notes[..3000] + "…";
        if (MessageBox.Show($"{notes}\n\nOpen the release page?", $"What's new in {UpdateChecker.Format(r.Version)}",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            OpenPage(r.PageUrl);
    }

    [RelayCommand]
    private void Later() => Available = null;

    [RelayCommand]
    private void Skip()
    {
        if (Available is not { } r) return;
        _ui.SkippedVersion = UpdateChecker.Format(r.Version);
        UiStateStore.Save(_ui);
        Available = null;
        _main.Status = $"Skipping {UpdateChecker.Format(r.Version)}. You will be told about the next release.";
    }

    [RelayCommand]
    private async Task UpdateNow()
    {
        if (Available is not { } release || IsBusy) return;
        if (_main.Host is { } host && (host.IsRunning || host.LaunchCommand.IsRunning))
        {
            MessageBox.Show("Stop the server before updating. Updating restarts ModderLords.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsDevBuild)
        {
            OpenPage(release.PageUrl);
            _main.Status = "This is a dev build, which is never updated in place; opened the release page instead.";
            return;
        }

        var installer = UpdateInstaller.ForThisApp();
        if (!installer.CanWriteInstallDir())
        {
            MessageBox.Show($"ModderLords cannot write to the folder it runs from:\n{installer.InstallDir}\n\n" +
                            "The release page will open instead. Unzip the new version over this one, or move ModderLords to a folder " +
                            "you own (for example C:\\Games\\ModderLords) so it can update itself next time.",
                "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenPage(release.PageUrl);
            return;
        }
        if (!_main.ResolveUnsavedChanges()) return;

        IsBusy = true;
        try
        {
            ProgressText = "Downloading…";
            using var http = UpdateInstaller.CreateDownloadClient();
            var zip = await installer.DownloadAndVerifyAsync(release, http,
                new Progress<double>(p => ProgressText = $"Downloading… {p:P0}"));
            ProgressText = "Installing…";
            var exe = await Task.Run(() => installer.Install(zip));

            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installer.InstallDir };
            start.ArgumentList.Add("--updated-from");
            start.ArgumentList.Add(UpdateChecker.Format(Running));
            Process.Start(start);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ProgressText = "";
            MessageBox.Show($"The update did not install: {ex.Message}\n\nModderLords is unchanged. You can download the new version " +
                            "from the release page instead.", "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; }
    }

    private void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(string.IsNullOrWhiteSpace(url) ? UpdateChecker.ReleasesPage : url) { UseShellExecute = true }); }
        catch (Exception ex) { _main.Status = "Could not open the release page: " + ex.Message; }
    }
}
