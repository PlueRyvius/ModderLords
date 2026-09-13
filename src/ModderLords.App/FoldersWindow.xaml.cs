using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ModderLords.Core.Launch;
using ModderLords.Coop.Launch;

namespace ModderLords.App;

/// <summary>
/// Edits the three locations a profile can pin: the game install, the coop dedicated server, and extra mod folders.
/// Until this existed the profile had all three fields and nothing in the app could set them, so anyone whose install
/// was not found automatically was simply stuck at "(game install not found)".
/// </summary>
public partial class FoldersWindow : Window
{
    public ObservableCollection<string> Roots { get; } = new();

    /// <summary>The game folder to pin, or null to find it automatically.</summary>
    public string? GameRoot => Normalise(GameBox.Text);

    /// <summary>The dedicated server folder to pin, or null to find it automatically.</summary>
    public string? ServerRoot => Normalise(ServerBox.Text);

    public IReadOnlyList<string> ModRoots => Roots.ToList();

    // Looked up once: what "empty" would resolve to, so the check line can say where the automatic choice points.
    private readonly string? _detectedGame;
    private readonly string? _detectedServer;
    private readonly bool _host;

    public FoldersWindow(string? gameRoot, string? serverRoot, IEnumerable<string> modRoots, bool host)
    {
        InitializeComponent();
        _host = host;
        _detectedGame = GamePaths.FindGameRoot();
        _detectedServer = host ? ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault() : null;
        ServerPanel.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        RootsHint.Text = ModderLords.App.ViewModels.FolderChecks.ModRootsHint;
        GameBox.Text = gameRoot ?? "";
        ServerBox.Text = serverRoot ?? "";
        foreach (var r in modRoots) Roots.Add(r);
        RootsList.ItemsSource = Roots;
        UpdateChecks();
    }

    private static string? Normalise(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Trim('"');

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged fires during InitializeComponent, before the check labels exist.
        if (GameCheck is not null && ServerCheck is not null) UpdateChecks();
    }

    private void UpdateChecks()
    {
        Show(GameCheck, ModderLords.App.ViewModels.FolderChecks.Game(GameRoot, _detectedGame));
        if (_host) Show(ServerCheck, ModderLords.App.ViewModels.FolderChecks.Server(ServerRoot, _detectedServer));
    }

    private void Show(TextBlock label, (bool Ok, string Text) check)
    {
        label.Text = check.Text;
        label.SetResourceReference(TextBlock.ForegroundProperty, check.Ok ? "MutedForeground" : "WarningForeground");
    }

    private static string? PickFolder(string title, string? start)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose the Bannerlord game folder (the one containing bin and Modules)", GameRoot ?? _detectedGame) is { } f) GameBox.Text = f;
    }

    private void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose the Coop DedicatedServer folder", ServerRoot ?? _detectedServer) is { } f) ServerBox.Text = f;
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Add a folder that holds mods", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var picked in dlg.FolderNames)
        {
            var root = ModderLords.App.ViewModels.FolderChecks.ModRootFor(picked);
            if (!Roots.Contains(root, StringComparer.OrdinalIgnoreCase)) Roots.Add(root);
        }
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string s) Roots.Remove(s);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Allowed, but not silently: a wrong game folder means the next scan finds no mods at all.
        var game = ModderLords.App.ViewModels.FolderChecks.Game(GameRoot, _detectedGame);
        if (GameRoot is not null && !game.Ok &&
            MessageBox.Show(game.Text + "\n\nUse this folder anyway?", "Folders", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
        Close();
    }
}
