using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;

namespace ModderLords.App;

/// <summary>One entry point as the authority window shows it.</summary>
public sealed record AuthorityRow(string Verdict, string Trigger, string Entry, string Method, string Reason, string Details, bool ActionNeeded)
{
    /// <summary>Rows for a report, action-needed verdicts first, then by entry point.</summary>
    public static IReadOnlyList<AuthorityRow> From(AuthorityReport report)
    {
        var actionNeeded = report.ActionNeeded.ToHashSet();
        return report.Roots
            .OrderBy(r => r.Verdict)
            .ThenBy(r => r.Root.Method, StringComparer.Ordinal)
            .Select(r => new AuthorityRow(
                r.Verdict.ToString(),
                r.Root.Trigger.ToString(),
                ShortName(r.Root.Method),
                r.Root.Method,
                r.Reason,
                DetailsFor(r),
                actionNeeded.Contains(r)))
            .ToList();
    }

    /// <summary>"Ns.Outer+Inner::Method" → "Outer+Inner.Method".</summary>
    public static string ShortName(string method)
    {
        var sep = method.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return method;
        var type = method[..sep];
        var dot = type.LastIndexOf('.');
        return (dot >= 0 ? type[(dot + 1)..] : type) + "." + method[(sep + 2)..];
    }

    private static string DetailsFor(RootVerdict r)
    {
        var lines = new List<string>
        {
            $"{r.Verdict}  ({r.Root.Trigger}{(string.IsNullOrEmpty(r.Root.Detail) ? "" : ": " + r.Root.Detail)})",
            r.Root.Method,
            r.Reason,
        };
        if (r.Root.Patch is { } p) lines.Add($"patches {p.TargetType}.{p.TargetMethod} ({p.Kind}{(p.Manual ? ", applied in code" : "")})");
        if (r.Evidence.Count > 0) lines.Add("via " + string.Join(" -> ", r.Evidence));
        if (r.Opaque) lines.Add("uses reflection, so some of what it calls is not visible to the analysis");
        foreach (var f in r.Flags ?? []) lines.Add("flag: " + f);
        if (r.GateInstead is { Count: > 0 } g) lines.Add("gated instead on clients: " + string.Join(", ", g.Select(ShortName)));
        if (r.RelayVia is { } via) lines.Add("relayed to the server via " + ShortName(via));
        if (r.SharedState is { Count: > 0 } st) lines.Add("shared mod state: " + string.Join(", ", st.Select(ShortName)) + " (static bool/number/string/enum fields are sent from the server to clients)");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Mods tab: where each of the selected mod's entry points has to run under Coop, from the static authority analysis
/// (the same classifier the launcher uses to generate recipes and the CLI's <c>authority</c> command prints).
/// </summary>
public partial class AuthorityWindow : Window
{
    private readonly DiscoveredModule _module;
    private IReadOnlyList<AuthorityRow> _rows = [];
    private AuthorityReport? _report;

    public AuthorityWindow(DiscoveredModule module)
    {
        InitializeComponent();
        _module = module;
        Title = $"Authority: {module.Id}";
        Header.Text = $"{module.Id} {module.Version}: analysing…";
        SubHeader.Text = "Reading the mod's code and Coop's; large mods take a few seconds.";
        Loaded += async (_, _) => await AnalyseAsync();
    }

    private async Task AnalyseAsync()
    {
        try
        {
            var module = _module;
            var (report, coopSummary) = await Task.Run(() =>
            {
                var gameInterface = CoopSinks.FindGameInterface(GamePaths.SteamLibraries())
                    ?? throw new InvalidOperationException("Coop's GameInterface.dll was not found (game Modules\\Coop or a Workshop item), so there is nothing to classify against.");
                var coop = CoopSinks.Load(gameInterface);
                return (AuthorityScan.Classify(ModAnalysis.Analyse(module), coop), coop.Summary);
            });
            _report = report;
            _rows = AuthorityRow.From(report);
            Header.Text = $"{_module.Id} {_module.Version}: {report.Summary}";
            SubHeader.Text = report.NotAnalysable
                ? "The mod's code could not be read, so recipes for it fall back to gating whole behaviours."
                : $"{_rows.Count(r => r.ActionNeeded)} of {_rows.Count} entry point(s) need attention. Against Coop: {coopSummary}.";
            ShowAll.IsEnabled = _rows.Count > 0;
            CopyButton.IsEnabled = true;
            ApplyFilter();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            Header.Text = $"{_module.Id} {_module.Version}: analysis failed";
            SubHeader.Text = ex.Message;
        }
    }

    private void ApplyFilter()
    {
        var shown = ShowAll.IsChecked == true ? _rows : _rows.Where(r => r.ActionNeeded).ToList();
        Grid.ItemsSource = shown;
        Details.Text = shown.Count == 0 && _rows.Count > 0 ? "Nothing needs attention. Tick \"Show every entry point\" to see them all." : "";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is AuthorityRow row) Details.Text = row.Details;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        try
        {
            Clipboard.SetText(_report.ToJson());
            CopyStatus.Text = "Report copied as JSON";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            CopyStatus.Text = "Could not copy: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
