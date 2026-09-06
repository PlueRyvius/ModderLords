using System.Windows;
using ModderLords.Core.Profiles;

namespace ModderLords.App;

/// <summary>
/// Asked once, on the first run after this version (or after a fresh data dir): mod loader, or mod loader plus
/// coop host? Closing it without choosing gives Player, because that is the smaller promise - it shows less, and
/// the toolbar button is one click away.
/// </summary>
public partial class FirstRunWindow : Window
{
    public AppMode ChosenMode { get; private set; } = AppMode.Player;

    public FirstRunWindow() => InitializeComponent();

    private void Player_Click(object sender, RoutedEventArgs e) { ChosenMode = AppMode.Player; DialogResult = true; }

    private void Host_Click(object sender, RoutedEventArgs e) { ChosenMode = AppMode.Host; DialogResult = true; }
}
