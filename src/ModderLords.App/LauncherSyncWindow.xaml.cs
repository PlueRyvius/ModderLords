using System.Windows;
using System.Windows.Controls;
using ModderLords.Core.Export;

namespace ModderLords.App;

/// <summary>
/// Shows exactly what Launch client is about to change in this PC's LauncherData.xml, before anything is written.
/// The file is the player's own single-player module list, so nothing happens without them seeing the diff once.
/// </summary>
public partial class LauncherSyncWindow : Window
{
    /// <summary>True when the player asked us to stop confirming for this profile.</summary>
    public bool DontAskAgain => DontAskBox.IsChecked == true;

    /// <param name="launching">
    /// True when this is Launch client, which starts the game straight after applying. False for Match server…,
    /// where nothing is launched — the button said "Apply and launch" there too, so the honest way to decline the
    /// launch was Cancel, which also declined the changes.
    /// </param>
    public LauncherSyncWindow(LauncherDataSync.SyncPlan plan, string launcherDataPath, string backupRoot, bool launching = true)
    {
        InitializeComponent();
        ApplyButton.Content = launching ? "Apply and launch" : "Apply";
        if (!plan.HasChanges) { ApplyButton.IsEnabled = launching; ApplyButton.Content = launching ? "Launch" : "Apply"; }
        SourceLine.Text = $"{launcherDataPath}\nOnly the ticks and the load order change. Your multiplayer list, the launcher's DLL list, and the official and Coop modules are left alone.";
        BackupLine.Text = "A copy of the current file is saved to " + backupRoot + " first.";

        Section("Enable", "Turn on — the server runs these", plan.Changes.Where(c => c.Action == LauncherDataSync.SyncAction.Enable));
        Section("Add", "Add to the list — installed, but the Bannerlord launcher has never listed them", plan.Changes.Where(c => c.Action == LauncherDataSync.SyncAction.Add));
        Section("Disable", "Turn off — the server does not run these, and Coop rejects extras", plan.Changes.Where(c => c.Action == LauncherDataSync.SyncAction.Disable));
        Section("Move", "Reorder to match the server's load order", plan.Changes.Where(c => c.Action == LauncherDataSync.SyncAction.Move));
        Section("Dup", "Remove duplicate entries — the Bannerlord launcher lists these twice", plan.Changes.Where(c => c.Action == LauncherDataSync.SyncAction.RemoveDuplicate));
        Section("Blocked", "Cannot be fixed from here — you will still be able to launch, but the join may be refused", plan.Blockers);
        if (!plan.HasChanges)
            Sections.Children.Add(new TextBlock { Text = "Your mod list already matches the server; nothing to change.", Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap });
    }

    private void Section(string _, string heading, IEnumerable<LauncherDataSync.PlannedChange> changes)
    {
        var rows = changes.ToList();
        if (rows.Count == 0) return;
        Sections.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap });
        foreach (var c in rows)
            Sections.Children.Add(new TextBlock { Text = $"    {c.Id}  —  {c.Detail}", FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
