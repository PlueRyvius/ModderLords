using System.Windows;
using ModularCoop.App.ViewModels;
using ModularCoop.Core.Compat;
using ModularCoop.Core.Saves;

namespace ModularCoop.App;

/// <summary>Edits the local compat record for one mod. The result goes to compat-db.local.json; the bundled file is never touched.</summary>
public partial class CompatRecordWindow : Window
{
    private readonly ModRow _row;
    private readonly string? _coopVersion;
    private readonly CompatRecord _record;

    /// <summary>The record to save (valid when DialogResult is true and RemoveLocal is false).</summary>
    public CompatRecord Result => _record;
    /// <summary>True when the user chose to delete the local record instead of saving one.</summary>
    public bool RemoveLocal { get; private set; }

    public CompatRecordWindow(ModRow row, string? coopVersion)
    {
        InitializeComponent();
        _row = row;
        _coopVersion = coopVersion;
        // Start from the record the badge shows (bundled or local) so recording a verdict keeps the notes and defaults already known.
        _record = row.Compat.Record?.Clone() ?? new CompatRecord { Id = row.Id };
        _record.Id = row.Id;
        _record.UpdatedAt = null; // stamped on save

        Header.Text = $"{row.Id} {row.Version}";
        SourceLine.Text = row.Compat.Source switch
        {
            CompatSource.Local => "Editing your local record (compat-db.local.json).",
            CompatSource.Bundled => "Starting from the record bundled with the launcher; saving creates a local record that overrides it.",
            _ => "No record yet; saving creates one in your local compat database.",
        };
        RemoveButton.IsEnabled = row.Compat.Source == CompatSource.Local;

        VerdictBox.ItemsSource = new[] { CompatVerdict.Works, CompatVerdict.NeedsRecipe, CompatVerdict.Broken, CompatVerdict.Unknown };
        VerdictBox.SelectedItem = _record.Verdict;

        TestedBox.Content = $"Tested with this version ({row.Version})" + (coopVersion is not null ? $" on Coop {coopVersion}" : "");
        TestedBox.IsChecked = _record.IsVersionTested(row.Version);
        TestedLine.Text = _record.TestedVersions.Count == 0 ? "No versions recorded yet." : "Recorded: " + string.Join(", ", _record.TestedVersions) + (_record.TestedCoopVersion is not null ? $" (Coop {_record.TestedCoopVersion})" : "");

        UrlBox.Text = _record.Url ?? "";
        NotesBox.Text = _record.Notes ?? "";
        ShowDefaults();
    }

    private void ShowDefaults()
    {
        if (_record.DefaultRole is null && _record.ServerAuthoritative is null && _record.ClientSideBehaviors.Count == 0)
        {
            DefaultsLine.Text = "None: new profiles use Run, server-only logic off.";
            return;
        }
        DefaultsLine.Text = $"Role {_record.DefaultRole?.ToString() ?? "(unset)"}, server-only logic {(_record.ServerAuthoritative is true ? "on" : _record.ServerAuthoritative is false ? "off" : "(unset)")}"
                            + (_record.ClientSideBehaviors.Count > 0 ? $"; client-side: {string.Join(", ", _record.ClientSideBehaviors)}" : "");
    }

    private void UseRow_Click(object sender, RoutedEventArgs e)
    {
        _record.DefaultRole = _row.Role;
        _record.ServerAuthoritative = _row.ServerAuthoritative;
        _record.ClientSideBehaviors = _row.ClientSideBehaviors.ToList();
        ShowDefaults();
    }

    private void ClearDefaults_Click(object sender, RoutedEventArgs e)
    {
        _record.DefaultRole = null;
        _record.ServerAuthoritative = null;
        _record.ClientSideBehaviors = new List<string>();
        ShowDefaults();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _record.Verdict = VerdictBox.SelectedItem is CompatVerdict v ? v : CompatVerdict.Unknown;
        _record.Url = string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim();
        _record.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        if (TestedBox.IsChecked == true)
        {
            if (!_record.IsVersionTested(_row.Version)) _record.TestedVersions.Add(_row.Version);
            if (_coopVersion is not null) _record.TestedCoopVersion = _coopVersion;
        }
        else
        {
            _record.TestedVersions.RemoveAll(t => SaveHeaderReader.VersionsEqual(t, _row.Version));
        }
        DialogResult = true;
        Close();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove your local record for {_row.Id}?", "Compatibility record", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RemoveLocal = true;
        DialogResult = true;
        Close();
    }
}
