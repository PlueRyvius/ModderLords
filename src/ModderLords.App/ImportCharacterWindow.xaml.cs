using System.Windows;
using ModderLords.Coop.Admin;

namespace ModderLords.App;

/// <summary>
/// Shows what the server's check said a character file would bring across (and what the sanitizer dropped or changed),
/// and lets the host pick the parts to import. Nothing is changed until Import.
/// </summary>
public partial class ImportCharacterWindow : Window
{
    public ImportCharacterWindow(ImportReport check, string targetName)
    {
        InitializeComponent();
        var exported = DateTimeOffset.TryParse(check.ExportedAt, out var at) ? $", exported {at.LocalDateTime:yyyy-MM-dd HH:mm}" : "";
        Header.Text = $"Import {check.SourceName} (level {check.SourceLevel}{exported}: {check.Skills} skills, {check.Perks} perks) onto {targetName}.";
        ModulesLine.Text = check.MissingModules.Count == 0 ? ""
            : $"Its world had mods this server does not run: {string.Join(", ", check.MissingModules)}. Whatever came from them is dropped below.";
        ModulesLine.Visibility = check.MissingModules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NotesHeader.Text = check.Notes.Count == 0
            ? "Everything in the file exists in this world and is within its limits."
            : $"Cleaned for this world ({check.Notes.Count}):";
        NotesList.ItemsSource = check.Notes;
    }

    /// <summary>The parts the host chose, as the server names them. Valid when DialogResult is true.</summary>
    public IReadOnlyList<string> SelectedParts { get; private set; } = [];

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var parts = new List<string>();
        if (Stats.IsChecked == true) parts.Add("stats");
        if (Gold.IsChecked == true) parts.Add("gold");
        if (Look.IsChecked == true) parts.Add("look");
        if (Gear.IsChecked == true) parts.Add("gear");
        if (NameBox.IsChecked == true) parts.Add("name");
        if (parts.Count == 0) { MessageBox.Show(this, "Pick at least one part to import.", "Import character"); return; }
        SelectedParts = parts;
        DialogResult = true;
    }
}
