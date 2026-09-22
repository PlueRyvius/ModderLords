using System.Windows;
using ModderLords.Core.Export;

namespace ModderLords.App;

/// <summary>
/// What to do with a mod list somebody shared. The same file serves a player who only wants their launcher set up
/// and a host who wants to run the same server, so both are offered and neither is assumed.
/// </summary>
public partial class ImportListWindow : Window
{
    public bool ApplyToLauncher => ApplyBox.IsChecked == true;
    public bool CreateProfile => ProfileBox.IsChecked == true;
    public string ProfileNameText => ProfileName.Text.Trim();
    public bool AutoSubscribe => SubscribeBox.IsChecked == true;

    public ImportListWindow(ModListFile file, string path, IReadOnlyList<string> existingProfiles, bool autoSubscribe = false)
    {
        InitializeComponent();
        SubscribeBox.IsChecked = autoSubscribe;
        Header.Text = $"{file.Mods.Count} mods" + (file.Name is null ? "" : $" from “{file.Name}”")
                      + (file.Coop is null ? "" : $", Coop {file.Coop.Version}");
        SourceLine.Text = $"{path}\nExported {file.ExportedAt.LocalDateTime:g}"
                          + (file.ExportedBy is null ? "" : $" by {file.ExportedBy}") + ". Listed in load order.";
        Listing.Text = string.Join("\n", file.Mods.Select((m, i) =>
            $"{i + 1,3}. {m.Id,-32} {m.Version,-14} {m.Role}{(m.ServerAuthoritative ? "  server-only logic" : "")}"));

        // Suggest a name that will not collide with a profile they already have.
        var suggested = string.IsNullOrWhiteSpace(file.Name) ? "shared" : file.Name!;
        var name = suggested;
        for (var n = 2; existingProfiles.Contains(name, StringComparer.OrdinalIgnoreCase); n++) name = $"{suggested} ({n})";
        ProfileName.Text = name;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToLauncher && !CreateProfile)
        {
            MessageBox.Show(this, "Pick at least one of the two, or cancel.", "Import a shared mod list",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CreateProfile && ProfileNameText.Length == 0)
        {
            MessageBox.Show(this, "Give the profile a name.", "Import a shared mod list", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }
}
