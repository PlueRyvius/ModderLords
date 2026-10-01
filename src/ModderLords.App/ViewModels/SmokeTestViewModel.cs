using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Profiles;
using ModderLords.Core.Smoke;

namespace ModderLords.App.ViewModels;

/// <summary>One row of the smoke test's checklist, as shown.</summary>
public sealed record SmokeCheckRow(string Verdict, string Name, string Detail, string Evidence, SmokeVerdict Kind)
{
    public bool HasEvidence => Evidence.Length > 0;
}

/// <summary>
/// The Smoke test tab: starts the server if it is not running, starts this PC's game so that it joins by itself, and
/// checks both sides until the game has stayed on the campaign map, then closes the game and shows the checklist.
/// The work is <see cref="SmokeRunner"/>; this only feeds it the Server tab's engine and shows what it says.
/// </summary>
public partial class SmokeTestViewModel : ObservableObject
{
    private readonly HostViewModel _host;
    private CancellationTokenSource? _cancel;

    public SmokeTestViewModel(HostViewModel host) => _host = host;

    public ObservableCollection<string> Progress { get; } = new();
    public ObservableCollection<SmokeCheckRow> Checks { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isTesting;

    [ObservableProperty] private string _steadySeconds = "60";
    [ObservableProperty] private bool _closeGameWhenDone = true;
    /// <summary>Run campaign time (Play_2x) while the game is on the map, so daily ticks, AI and mods actually run.</summary>
    [ObservableProperty] private bool _runCampaignTime = true;
    [ObservableProperty] private bool _stopServerIfStartedHere = true;
    [ObservableProperty] private string _headline = "Not run yet.";
    [ObservableProperty] private SmokeVerdict? _overall;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private SmokeReport? _report;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        Progress.Clear();
        Checks.Clear();
        Report = null;
        Overall = null;
        if (ClientLauncher.IsClientRunning()) { Headline = "Bannerlord is already running on this PC. Close it, then run the test."; return; }
        if (_host.ClientProfile.Server.Password.Length > 0) { Headline = "This server has a password, and Coop's automatic join cannot send one. Clear it on the Server tab for the test."; return; }
        if (!int.TryParse(SteadySeconds, out var steady) || steady < 10) { Headline = "Seconds on the map: a number, 10 or more."; return; }
        var gameRoot = ClientLauncher.ResolveGameRoot(_host.ClientProfile);
        if (gameRoot is null) { Headline = "No Bannerlord install found for this profile."; return; }

        IsTesting = true;
        Headline = "Running…";
        _cancel = new CancellationTokenSource();
        var dispatcher = Application.Current.Dispatcher;
        var runner = new SmokeRunner(new SmokeOptions
        {
            GameRoot = gameRoot,
            JoinPort = _host.ClientProfile.Server.JoinPort,
            SteadyFor = TimeSpan.FromSeconds(steady),
            CloseClientWhenDone = CloseGameWhenDone,
            SendServerCommand = RunCampaignTime ? command => dispatcher.Invoke(() => _host.SendServerCommandAsync(command)) : null,
            ReportRoot = Path.Combine(ProfileStore.RootDir, "smoke"),
        });
        runner.Progress += line => dispatcher.BeginInvoke(() => Progress.Add($"{DateTime.Now:HH:mm:ss}  {line}"));
        void OnLine(string text) => runner.ServerLine(text);
        void OnExit(int code) => runner.ServerExited(code);
        _host.ServerLineObserved += OnLine;
        _host.ServerExited += OnExit;

        var startedServer = false;
        try
        {
            if (_host.IsRunning)
            {
                if (_host.IsServing) runner.ServerAlreadyServing();
                Progress.Add($"{DateTime.Now:HH:mm:ss}  Using the server that is already running.");
            }
            else
            {
                Progress.Add($"{DateTime.Now:HH:mm:ss}  Starting the server (as the Server tab's Start would)…");
                startedServer = true;
                // Launch runs until the engine exits; the test only needs it started.
                _ = _host.LaunchCommand.ExecuteAsync(null);
            }

            var report = await Task.Run(() => runner.RunAsync(StartClient, _cancel.Token));
            Show(report);
            _host.AddLine(report.Overall == SmokeVerdict.Fail ? LogCategory.Error : LogCategory.Tool,
                $"[ModderLords] smoke test {report.Headline}" + (report.Folder is null ? "" : $" ({report.Folder})"));
        }
        catch (Exception ex)
        {
            Headline = "The smoke test stopped: " + ex.Message;
            _host.AddLine(LogCategory.Error, "[ModderLords] smoke test: " + ex);
        }
        finally
        {
            _host.ServerLineObserved -= OnLine;
            _host.ServerExited -= OnExit;
            IsTesting = false;
            _cancel.Dispose();
            _cancel = null;
        }
        if (startedServer && StopServerIfStartedHere && _host.IsRunning && _host.StopCommand.CanExecute(null))
        {
            Progress.Add($"{DateTime.Now:HH:mm:ss}  Stopping the server the test started…");
            await _host.StopCommand.ExecuteAsync(null);
        }

        // Called by the runner once the server serves: the same client plan as Launch client, plus Coop's auto-join.
        Process StartClient(IReadOnlyList<string> extraArguments) => dispatcher.Invoke(() =>
        {
            var target = _host.ClientTarget ?? throw new InvalidOperationException("The server has no client plan yet; rescan the mods.");
            _host.EnsureClientModule(HostViewModel.ServerCarriesSyncModule(target));
            var client = _host.PrepareClientLaunch();
            foreach (var message in client.Messages) Progress.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            return ClientLaunchSession.Start(client.Plan with { ExtraArguments = extraArguments });
        });
    }

    private bool CanRun() => !IsTesting;

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        if (Report?.Folder is { } folder && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (Report is null) return;
        try { Clipboard.SetText(Report.ToText()); Headline = Report.Headline + " — report copied."; }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private bool HasReport() => Report is not null;

    private void Show(SmokeReport report)
    {
        Report = report;
        Overall = report.Overall;
        Headline = $"{report.Headline} in {report.Duration:mm\\:ss}";
        foreach (var c in report.Checks)
            Checks.Add(new SmokeCheckRow(SmokeReport.Label(c.Verdict).ToUpperInvariant(), c.Name, c.Detail, string.Join(Environment.NewLine, c.Evidence), c.Verdict));
        foreach (var note in report.Notes) Progress.Add($"{DateTime.Now:HH:mm:ss}  {note}");
    }
}
