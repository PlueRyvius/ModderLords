using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ModularCoop.Core.Compat;

namespace ModularCoop.App;

public partial class BehaviorRow : ObservableObject
{
    public required string TypeName { get; init; }
    public required string Kind { get; init; }
    [ObservableProperty] private bool _serverOnly = true;
}

/// <summary>Per-mod editor: which scanned behaviours the recipe gates on clients.</summary>
public partial class BehaviorsWindow : Window
{
    public ObservableCollection<BehaviorRow> Rows { get; } = new();

    /// <summary>Behaviours the user wants to keep on clients (the profile stores this list; empty = everything server-only).</summary>
    public List<string> ClientSide => Rows.Where(r => !r.ServerOnly).Select(r => r.TypeName).ToList();

    public BehaviorsWindow(string modId, ScanResult scan, IReadOnlyCollection<string> currentClientSide)
    {
        InitializeComponent();
        Header.Text = $"{modId}: {scan.CampaignBehaviors.Count} campaign behaviour(s), {scan.MissionBehaviors.Count} mission behaviour(s)";
        foreach (var b in scan.CampaignBehaviors) Rows.Add(new BehaviorRow { TypeName = b, Kind = "campaign", ServerOnly = !currentClientSide.Contains(b) });
        foreach (var b in scan.MissionBehaviors) Rows.Add(new BehaviorRow { TypeName = b, Kind = "mission", ServerOnly = !currentClientSide.Contains(b) });
        Grid.ItemsSource = Rows;
    }

    private void All_Click(object sender, RoutedEventArgs e) { foreach (var r in Rows) r.ServerOnly = true; }
    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
