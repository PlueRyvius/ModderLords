using System.Windows;
using ModderLords.Core.Workshop;

namespace ModderLords.App;

/// <summary>
/// Where a mod can be downloaded, as the exported list will tell whoever imports it. A mod installed by hand into the
/// game's Modules folder has no link of its own, so without this an importer is left searching for it by name.
/// </summary>
public partial class SourceLinkWindow : Window
{
    /// <summary>The link as entered; empty means "no link".</summary>
    public string Link => LinkBox.Text.Trim();

    public SourceLinkWindow(string modId, string? current)
    {
        InitializeComponent();
        PromptText.Text = $"Where can {modId} be downloaded? Shared mod lists carry this link, and a Steam Workshop link lets importers subscribe automatically.";
        LinkBox.Text = current ?? "";
        LinkChanged(this, null!);
        Loaded += (_, _) => { LinkBox.Focus(); LinkBox.SelectAll(); };
    }

    private void LinkChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (LinkBox is null || HintText is null) return;
        HintText.Text = Link.Length == 0 ? "Empty removes the link."
            : WorkshopEvent.ParseWorkshopId(Link) is { } id ? $"Steam Workshop item {id}: importers can subscribe to it automatically."
            : "Not a Steam Workshop link: importers will be shown it to download by hand.";
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
