using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.App.Localization;
using ModderLords.Coop.Testing;
using ModderLords.Core.Launch;
using ModderLords.Core.Testing;

namespace ModderLords.App.ViewModels;

/// <summary>One test module in the Tests tab's list.</summary>
public sealed partial class TestModuleRow : ObservableObject
{
    public TestModuleRow(TestModule module)
    {
        Id = module.Id;
        Title = module.Title;
        Description = module.Description;
        Needs = module.Requires.Count == 0 ? "" : Loc.T("Needs: ") + string.Join(", ", module.Requires);
        IsCore = module.Id == SuiteSelection.CoreSuite;
        _isPicked = IsCore;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Needs { get; }
    public bool HasNeeds => Needs.Length > 0;
    /// <summary>The core checks always run; their box is ticked and cannot be cleared.</summary>
    public bool IsCore { get; }
    public bool CanPick => !IsCore && IsAvailable;

    [ObservableProperty] private bool _isPicked;

    /// <summary>Empty when this profile can run it; otherwise what it lacks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    private string _unavailable = "";

    public bool IsAvailable => Unavailable.Length == 0;
}

/// <summary>One suite's outcome after a run.</summary>
public sealed record TestSuiteRow(string Suite, string Verdict);

/// <summary>One step's outcome after a run, as the report shows it.</summary>
public sealed record TestStepRow(string Verdict, string Suite, string Step, string Side, string Message);

/// <summary>
/// The Tests tab (docs/INGAME-TEST-HARNESS.md, decision 31): pick test modules, single player or Coop, run, watch the
/// progress, then read a pass or fail per suite. The run is <see cref="InGameTestRun"/>, the same one the CLI's
/// <c>test</c> command uses, on the saved profile.
/// </summary>
public partial class TestsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cancel;
    private InGameTestResult? _result;

    public TestsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var module in TestSuites.Modules()) Modules.Add(new TestModuleRow(module));
    }

    /// <summary>The profile changed: say again which modules it can run (cheap with the app's scanned catalogue).</summary>
    internal void ProfileChanged()
    {
        // In the app only: the check scans mods, and its results are shown through the UI thread.
        if (!IsTesting && Application.Current is not null) _ = RefreshCommand.ExecuteAsync(null);
    }

    public ObservableCollection<TestModuleRow> Modules { get; } = new();
    public ObservableCollection<string> Worlds { get; } = new();
    public ObservableCollection<string> Progress { get; } = new();
    public ObservableCollection<TestSuiteRow> Suites { get; } = new();
    public ObservableCollection<TestStepRow> Steps { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCoop))]
    private bool _singlePlayer = true;

    public bool IsCoop { get => !SinglePlayer; set => SinglePlayer = !value; }

    /// <summary>The test world a run starts from (test-worlds\NAME.sav); empty: single player makes its own.</summary>
    [ObservableProperty] private string _world = "";
    [ObservableProperty] private bool _keepOpenOnFail;
    [ObservableProperty] private string _headline = "Pick the modules to test, then Run.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRunFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasResult;

    /// <summary>Lists the modules and test worlds, then checks in the background which modules this profile can run.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Refresh()
    {
        var picked = Modules.Where(m => m.IsPicked).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Modules.Clear();
        foreach (var module in TestSuites.Modules())
            Modules.Add(new TestModuleRow(module) { IsPicked = module.Id == SuiteSelection.CoreSuite || picked.Contains(module.Id) });
        Worlds.Clear();
        Worlds.Add("");
        foreach (var w in InGameTestRun.TestWorlds()) Worlds.Add(w);
        if (!Worlds.Contains(World)) World = "";

        var profile = _main.Profile;
        var single = SinglePlayer;
        var scanned = _main.ScannedCatalog;
        var notes = new List<string>();
        var mods = await Task.Run(() => InGameTestRun.InstalledMods(profile, single, notes.Add, scanned));
        foreach (var note in notes) Progress.Add(note);
        if (mods is null) return;
        foreach (var row in Modules)
        {
            var module = TestSuites.Module(row.Id)!;
            var unmet = SuiteSelection.Unmet(module.Requires, mods);
            row.Unavailable = unmet.Count == 0 ? "" : Loc.T("Unavailable: needs ") + string.Join(", ", unmet.Select(r => SuiteSelection.Describe(r, mods)));
            if (!row.IsAvailable) row.IsPicked = false;
        }
    }

    partial void OnSinglePlayerChanged(bool value) => ProfileChanged();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        Progress.Clear();
        Suites.Clear();
        Steps.Clear();
        HasResult = false;
        _result = null;
        // The run reads the profile from disk, as the CLI does: unsaved changes would test something else.
        if (_main.IsDirty) { Headline = Loc.T("Save the profile first: a test run uses the profile as saved."); return; }
        if (!SinglePlayer && World.Length == 0)
        {
            Headline = Loc.T("Coop tests change the world they run on. Pick a test world (a copy is used each time), so your campaign is never touched.");
            return;
        }
        if (!SinglePlayer && _main.Host?.IsRunning == true) { Headline = Loc.T("Stop the server on the Server tab first: a Coop test starts its own."); return; }
        if (ClientLauncher.IsClientRunning()) { Headline = Loc.T("Bannerlord is already running on this PC. Close it, then run the test."); return; }

        var options = new InGameTestOptions
        {
            ProfileName = _main.Profile.Name,
            SinglePlayer = SinglePlayer,
            // Core always runs; the list is the other modules ticked.
            Modules = Modules.Where(m => m.IsPicked && !m.IsCore).Select(m => m.Id).ToList(),
            World = World.Length == 0 ? null : World,
            KeepOpenOnFail = KeepOpenOnFail,
        };
        IsTesting = true;
        Headline = Loc.T("Running… don't use the PC: the game window comes to the front.");
        _cancel = new CancellationTokenSource();
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var token = _cancel.Token;
            _result = await Task.Run(() => InGameTestRun.RunAsync(options, line => dispatcher.BeginInvoke(() => Progress.Add(line)), token));
            Show(_result);
        }
        catch (Exception ex) { Headline = Loc.T("The test run stopped: ") + ex.Message; }
        finally
        {
            IsTesting = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    private bool CanRun() => !IsTesting;

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void OpenRunFolder()
    {
        if (_result?.RunFolders.LastOrDefault() is { } folder && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyReport()
    {
        if (_result is null) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _result.Reports.Select(r => r.ToText()))); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Show(InGameTestResult result)
    {
        foreach (var report in result.Reports)
        {
            foreach (var (suite, passed) in report.Suites) Suites.Add(new TestSuiteRow(suite, passed ? "PASS" : "FAIL"));
            foreach (var step in report.Steps)
                Steps.Add(new TestStepRow(Mark(step.Outcome), step.Suite, step.Step, step.Side, step.Message));
        }
        var folders = result.RunFolders.Count;
        HasResult = result.Reports.Count > 0 || folders > 0;
        Headline = result.Problem is { } problem && result.Reports.Count == 0
            ? Loc.T("Not run: ") + problem
            : (result.Passed ? Loc.T("PASS") : Loc.T("FAIL")) + Loc.F(" — {0} suite(s) in {1} launch(es)", Suites.Count, Math.Max(1, result.Reports.Count));
    }

    private static string Mark(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Pass => "PASS",
        StepOutcome.KnownFail => "KNOWN",
        StepOutcome.Skipped => "SKIP",
        StepOutcome.UnexpectedPass => "PASS?",
        StepOutcome.NoResult => "NONE",
        _ => "FAIL",
    };
}
