using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ModderLords.Analysis;

namespace ModderLords.App;

public sealed class OperationAnalysisWindow : Window
{
    public OperationAnalysisWindow(AnalysisReport report)
    {
        Title = "Operation compatibility — selected launch snapshot"; Width = 1100; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(16) };
        var summary = new TextBlock { Text = report.Summary + "\nEvidence describes possible effects, not proof of working multiplayer.", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(summary, Dock.Top); dock.Children.Add(summary);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Contracts", Content = GridFor(report.Plan.Contracts.Select(c => new
        { Feature = c.Contract.Operation, c.Contract.Provider, Decision = c.Decision.ToString(), c.Reason, c.Contract.ActorBinding, c.Contract.Transport })) });
        var split = new Grid(); split.RowDefinitions.Add(new RowDefinition()); split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });
        var operations = GridFor(report.Operations.Select(o => new OperationRow(o))); split.Children.Add(operations);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(details, 1); split.Children.Add(details);
        operations.SelectionChanged += (_, _) =>
        {
            if (operations.SelectedItem is not OperationRow row) return;
            var o = row.Operation;
            details.Text = $"Loading: {o.Status.Loading}; authority: {o.Status.Authority}; interaction: {o.Status.Interaction}; replication: {o.Status.Replication}\nOffline: {o.Status.OfflineVerification}; runtime: {o.Status.RuntimeVerification}\n\n" +
                string.Join("\n\n", o.Evidence.Select(e => $"{e.Effect}\n{string.Join(" → ", e.CallChain)}\n{e.MethodIdentity}, IL_{e.IlOffset:X4}: {e.Detail}"));
        };
        tabs.Items.Add(new TabItem { Header = "Operations and evidence", Content = split });
        tabs.Items.Add(new TabItem { Header = "Coverage gaps", Content = GridFor(report.Gaps) });
        dock.Children.Add(tabs); Content = dock;
    }
    private sealed class OperationRow(OperationFinding operation)
    {
        public OperationFinding Operation { get; } = operation;
        public string Module => Operation.Module;
        public string Side => Operation.Side.ToString();
        public string EntryPoint => Operation.EntryPoint;
        public string Authority => Operation.Authority.ToString();
        public string Effects => string.Join(", ", Operation.Effects);
    }
    private static DataGrid GridFor(System.Collections.IEnumerable items)
    {
        var grid = new DataGrid { ItemsSource = items, IsReadOnly = true, AutoGenerateColumns = true,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        grid.AutoGeneratingColumn += (_, e) => { if (e.PropertyName == "Operation") e.Cancel = true; };
        return grid;
    }
}
