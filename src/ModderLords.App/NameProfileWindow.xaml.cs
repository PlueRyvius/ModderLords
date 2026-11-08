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
    /// <summary>Why a name cannot be used, or null; a profile's file-name rules unless the caller asks for others (a group's).</summary>
    private readonly Func<string, string?> _problemOf;

    public string ProfileName => NameBox.Text.Trim();

    public NameProfileWindow(string prompt, string initial, string? currentName = null, Func<string, string?>? problemOf = null)
    {
        InitializeComponent();
        _currentName = currentName;
        _problemOf = problemOf ?? (name => ProfileStore.NameProblem(name, _currentName));
        PromptText.Text = prompt;
        NameBox.Text = initial;
        NameChanged(this, null!);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void NameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Built in the constructor before the fields exist on the first call.
        if (NameBox is null || OkButton is null || ProblemText is null) return;
        var problem = _problemOf(NameBox.Text);
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (_problemOf(NameBox.Text) is not null) return;
        DialogResult = true;
    }
}
