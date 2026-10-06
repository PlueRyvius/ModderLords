using System.Windows;
using ModderLords.App.ViewModels;

namespace ModderLords.App;

/// <summary>
/// Which side each top-level folder of one mod is for: the dedicated server, the player's game, or both. The
/// setting describes the folder rather than this PC, because a profile is shared and whoever receives it may host
/// or may play. Everything it decides is in <see cref="ModFolderChoices"/>; this window only shows the rows.
/// </summary>
public partial class ModFoldersWindow : Window
{
    public ModFoldersWindow(string modId, ModFolderChoices choices)
    {
        InitializeComponent();
        Header.Text = $"{modId}: {choices.Rows.Count(r => r.IsPresent)} folder(s)";
        IntroText.Text =
            $"Choose who uses each folder of this mod. {ModFolderRow.BothLabel}: the dedicated server and the game both get it. "
            + $"{ModFolderRow.ServerOnlyLabel}: the game is started without it. {ModFolderRow.ClientOnlyLabel}: the dedicated server is started without it. "
            + "The mod's own files are never changed; a folder is only left out of what the server or the game is shown. "
            + $"{ModFolderRow.ServerOnlyLabel} applies to games started from ModderLords, on this PC or by anyone using this profile. "
            + "A game started from Steam still sees every folder.";
        Grid.ItemsSource = choices.Rows;
        if (choices.Rows.Count == 0)
        {
            Grid.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
