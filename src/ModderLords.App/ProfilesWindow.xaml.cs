using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModderLords.App.Localization;
using ModderLords.App.ViewModels;

namespace ModderLords.App;

/// <summary>
/// The Profiles window (docs/PROFILE-MANAGER.md, decision 3). The work is <see cref="ProfilesViewModel"/>; this adds
/// what only a window can do: drag a profile onto a group, fill the "Move to group" submenu, focus the search box.
/// </summary>
public partial class ProfilesWindow : Window
{
    private readonly ProfilesViewModel _vm;
    private Point? _dragStart;

    public ProfilesWindow(ProfilesViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.Used += Close;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Profile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Selected is not null) _vm.UseCommand.Execute(null);
    }

    private void Profile_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Profile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _vm.Selected is not { } row) return;
        var moved = e.GetPosition(null) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(ProfileList, row, DragDropEffects.Move);
    }

    private void Group_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ProfileRow)) && GroupUnder(e) is { } g && g.Key != ProfileGroupRow.AllKey ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Group_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ProfileRow)) is ProfileRow row && GroupUnder(e) is { } group) _vm.MoveTo(row, group.Key);
    }

    private ProfileGroupRow? GroupUnder(DragEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is ListBoxItem { DataContext: ProfileGroupRow group }) return group;
        return null;
    }

    /// <summary>Only a group the user made can be renamed or deleted; the others say so instead.</summary>
    private void GroupMenu_Opened(object sender, RoutedEventArgs e)
    {
        var named = _vm.SelectedGroup is { IsNamed: true };
        RenameGroupItem.Visibility = DeleteGroupItem.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
        NoGroupMenuItem.Visibility = named ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Fills the right-click menu for the selected profile: Pin or Unpin, and every group to move it to. A deleted
    /// profile gets Restore alone.
    /// </summary>
    private void ProfileMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        var deleted = row.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        RestoreItem.Visibility = row.IsDeleted ? Visibility.Visible : Visibility.Collapsed;
        UseItem.Visibility = RenameItem.Visibility = DuplicateItem.Visibility = PinItem.Visibility = MoveToItem.Visibility =
            DeleteSeparator.Visibility = DeleteItem.Visibility = deleted;
        if (row.IsDeleted) return;
        PinItem.Header = row.Pinned ? Loc.T("Unpin") : Loc.T("Pin");
        MoveToItem.Items.Clear();
        foreach (var group in _vm.MoveTargets())
        {
            var label = group.Length == 0 ? Loc.T("Ungrouped") : group;
            var item = new MenuItem { Header = label, IsChecked = row.Group.Equals(group, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _vm.MoveTo(row, group);
            MoveToItem.Items.Add(item);
        }
        MoveToItem.Items.Add(new Separator());
        MoveToItem.Items.Add(new MenuItem { Header = Loc.T("New group…"), Command = _vm.MoveToNewGroupCommand });
    }
}
