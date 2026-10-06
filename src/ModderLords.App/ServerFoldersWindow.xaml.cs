using System.Windows;
using ModderLords.App.ViewModels;
using ModderLords.Core.Overlay;

namespace ModderLords.App;

/// <summary>
/// Which top-level folders of one mod the dedicated server is not shown. Per mod and off unless a host asks: the
/// suspicion that a client-only folder (RuntimeDataCache) hangs the server is unproven, so this is a way to test it,
/// not a default. OK is not the default button, because Enter has to make a new line in the list.
/// </summary>
public partial class ServerFoldersWindow : Window
{
    private readonly IReadOnlyList<string> _recordFolders;

    /// <summary>What to store on the profile: null = no opinion (the compat record decides), empty = leave nothing out.</summary>
    public List<string>? Result => MainViewModel.ServerFoldersChoice(FoldersBox.Text, UseRecordBox.IsChecked == true, _recordFolders.Count > 0);

    /// <param name="current">The profile's own list for this mod; null when it has none.</param>
    /// <param name="recordFolders">The compat record's list, which applies while the profile has none.</param>
    public ServerFoldersWindow(string modId, IReadOnlyList<string>? current, IReadOnlyList<string> recordFolders)
    {
        InitializeComponent();
        _recordFolders = recordFolders;
        PromptText.Text = $"Folders of {modId} the dedicated server should not see, one name per line. "
                        + "This is for client-only folders such as RuntimeDataCache: the server is given the mod without them, "
                        + "and the mod's own folder is never changed. Players are not affected.";
        // The tick only means something when there is a record list to fall back to; without one it would be a
        // second way of saying "empty".
        UseRecordBox.Visibility = recordFolders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UseRecordBox.Content = "Use the compatibility database's list for this mod (" + string.Join(", ", recordFolders) + ")";
        UseRecordBox.IsChecked = current is null && recordFolders.Count > 0;
        FoldersBox.Text = UseRecordBox.IsChecked == true ? string.Join(Environment.NewLine, recordFolders) : string.Join(Environment.NewLine, current ?? []);
        FoldersBox.IsEnabled = UseRecordBox.IsChecked != true;
        FoldersChanged(this, null!);
        Loaded += (_, _) => { if (FoldersBox.IsEnabled) FoldersBox.Focus(); };
    }

    private void UseRecordChanged(object sender, RoutedEventArgs e)
    {
        if (FoldersBox is null) return;
        var useRecord = UseRecordBox.IsChecked == true;
        FoldersBox.IsEnabled = !useRecord;
        if (useRecord) FoldersBox.Text = string.Join(Environment.NewLine, _recordFolders);
        FoldersChanged(this, null!);
    }

    private void FoldersChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (FoldersBox is null || HintText is null || UseRecordBox is null) return;
        HintText.Text = MainViewModel.ServerFoldersHint(FoldersBox.Text, UseRecordBox.IsChecked == true, _recordFolders.Count > 0);
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
