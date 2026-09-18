using System.Windows;
using ModderLords.Core.Profiles;

namespace ModderLords.App;

/// <summary>
/// Asks for a profile name, for New and for Rename. Validation lives in <see cref="ProfileStore.NameProblem"/> so
/// the dialog and the store cannot disagree about what a usable name is.
/// </summary>
public partial class NameProfileWindow : Window
{
    /// <summary>The name being renamed, so changing only its capitalisation is not reported as a collision.</summary>
    private readonly string? _currentName;

    public string ProfileName => NameBox.Text.Trim();

    public NameProfileWindow(string prompt, string initial, string? currentName = null)
    {
        InitializeComponent();
        _currentName = currentName;
        PromptText.Text = prompt;
        NameBox.Text = initial;
        NameChanged(this, null!);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void NameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Built in the constructor before the fields exist on the first call.
        if (NameBox is null || OkButton is null || ProblemText is null) return;
        var problem = ProfileStore.NameProblem(NameBox.Text, _currentName);
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (ProfileStore.NameProblem(NameBox.Text, _currentName) is not null) return;
        DialogResult = true;
    }
}
