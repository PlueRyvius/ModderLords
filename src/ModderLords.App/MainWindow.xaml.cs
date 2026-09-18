using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ModderLords.App.ViewModels;
using ModderLords.Core.Profiles;

namespace ModderLords.App;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private readonly UiState _ui;
    /// <summary>Set while the saved state is being applied, so restoring a tab or a theme is not written straight
    /// back as if the user had chosen it.</summary>
    private bool _restoring;

    public MainWindow()
    {
        InitializeComponent();
        // The version comes from Directory.Build.props, so a local build shows the real number rather than 1.0.0.
        // Debug builds say so: a screenshot from an unreleased build should never look like a release.
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v is not null) Title += $"  v{v.Major}.{v.Minor}.{v.Build}";
#if DEBUG
        Title += " (dev build)";
#endif
        _ui = UiStateStore.Load();
        RestoreGeometry();
        RestoreTheme();

        // The mode decides which half of the app is even constructed, so it is settled before the first scan.
        // A state file with no mode is a first run (or a fresh data dir): ask, once, and remember the answer.
        var mode = _ui.Mode;
        if (mode is null) mode = AskForMode();
        ViewModel.ExperimentalCompat = _ui.ExperimentalCompat;
        ViewModel.ApplyMode(mode.Value);
        _baseTitle = Title;
        ViewModel.Update = new UpdateViewModel(ViewModel, _ui);
        if (App.UpdatedFrom is { } from)
            ViewModel.Status = $"Updated to {ModderLords.Core.Updates.UpdateChecker.Format(UpdateViewModel.Running)} from {from}.";
        // After the first frame: the check is a network call, and the window must never wait on the network to appear.
        ContentRendered += async (_, _) =>
        {
            try { if (ViewModel.Update is { } update) await update.CheckOnStartupAsync(); }
            catch (Exception) { }
        };
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        AttachHost();
        UpdateModeButton();
        RestoreSelectedTab();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// The first-run choice, shown from this window's constructor - which is before the window is shown, so it is
    /// the application's only window while it is up. Under the default ShutdownMode that makes closing it the last
    /// window closing, and WPF ends the process before ModderLords has drawn a frame; hence the explicit shutdown
    /// mode across the dialog.
    /// </summary>
    private AppMode AskForMode()
    {
        var app = Application.Current;
        var previous = app?.ShutdownMode ?? ShutdownMode.OnLastWindowClose;
        if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var dlg = new FirstRunWindow();
            dlg.ShowDialog();
            _ui.Mode = dlg.ChosenMode;
            UiStateStore.Save(_ui);
            return dlg.ChosenMode;
        }
        finally { if (app is not null) app.ShutdownMode = previous; }
    }

    // ---- mode ------------------------------------------------------------------------------------------

    /// <summary>The title without the unsaved-changes marker, so the marker can be added and taken away.</summary>
    private string _baseTitle = "";

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDirty)) Title = ViewModel.IsDirty ? "* " + _baseTitle : _baseTitle;
        if (e.PropertyName == nameof(MainViewModel.Host)) AttachHost();
        if (e.PropertyName == nameof(MainViewModel.ExperimentalCompat))
        {
            ApplyModeToColumns(ViewModel.Mode == AppMode.Host);
            _ui.ExperimentalCompat = ViewModel.ExperimentalCompat;
            UiStateStore.Save(_ui);
        }
        if (e.PropertyName != nameof(MainViewModel.Mode)) return;
        UpdateModeButton();
        _ui.Mode = ViewModel.Mode;
        UiStateStore.Save(_ui);
        // The tabs either side of a switch are different, so land on the first one that is actually visible
        // rather than leaving the selection on a tab that has just been collapsed.
        if (Tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible } or null)
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
    }

    /// <summary>
    /// One click, no restart - that was the requirement. Switching to Host builds the host view model the first
    /// time; switching back to Player only hides it, because a running server must survive the trip.
    /// </summary>
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Mode == AppMode.Host && ViewModel.Host is { } host && (host.IsRunning || host.LaunchCommand.IsRunning))
        {
            MessageBox.Show("Stop the server before switching to Player mode.", "ModderLords",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ViewModel.Mode = ViewModel.Mode == AppMode.Player ? AppMode.Host : AppMode.Player;
    }

    /// <summary>
    /// Hides the dedicated-server columns of the mod grid in Player mode. DataGrid columns are not part of the
    /// visual tree, so they inherit no DataContext and Visibility cannot simply be bound the way it is on the
    /// buttons beside them; they are toggled by name instead.
    /// </summary>
    private void ApplyModeToColumns(bool host)
    {
        var v = host ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in new System.Windows.Controls.DataGridColumn[] { RoleColumn, CompatColumn, BinsColumn, NotesColumn, ServerVerdictColumn })
            c.Visibility = v;
        // Experimental compatibility (Server tab, Advanced) owns these; hidden unless it is on.
        var x = host && ViewModel.ExperimentalCompat ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in new System.Windows.Controls.DataGridColumn[] { ServerOnlyColumn, BehavioursColumn })
            c.Visibility = x;
    }

    private void UpdateModeButton()
    {
        var host = ViewModel.Mode == AppMode.Host;
        ApplyModeToColumns(host);
        ModeButton.Content = host ? "Host mode" : "Player mode";
        ModeButton.ToolTip = host
            ? "Hosting a dedicated coop server. Click to go back to Player mode, which is just the mod loader."
            : "Launching your own game with your mods. Click to switch to Host mode and run a coop server.";
    }

    /// <summary>The console lives on the host view model, so its scroll wiring is (re)attached when that is built.</summary>
    private void AttachHost()
    {
        if (ViewModel.Host is not { } host) return;
        // Auto-scroll means auto-scroll: the tick follows the tail wherever you are, and unticking is how you stop
        // it to read something. It used to also require you to already be at the bottom, so scrolling up silently
        // disabled it and ticking the box while scrolled up appeared to do nothing at all.
        host.ConsoleFlushed += () =>
        {
            if (host.AutoScroll) FindScrollViewer(ConsoleList)?.ScrollToEnd();
        };
        // Ticking the box jumps to the bottom straight away, rather than waiting for the server to say something.
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostViewModel.AutoScroll) && host.AutoScroll)
                Dispatcher.BeginInvoke(() => FindScrollViewer(ConsoleList)?.ScrollToEnd());
        };
    }

    // ---- dragging the mod order -------------------------------------------------------------------------

    private Point _dragStart;
    private ModRow? _dragRow;
    private InsertionAdorner? _insertion;

    /// <summary>
    /// Remembers where a press landed, so a click that turns into a drag can be told from one that does not. The
    /// row is captured here rather than on move because by then the grid may have changed the selection.
    /// </summary>
    private void ModsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = RowUnder(e.OriginalSource as DependencyObject)?.Item as ModRow;
        // The game's own modules are placed by the engine, so there is nothing to drag.
        if (_dragRow is { IsGameModule: true }) _dragRow = null;
        // A press on the enabled tick or the role dropdown is an edit, not a drag; those handle themselves.
        if (e.OriginalSource is DependencyObject d && (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(d) is not null
                                                       || FindAncestor<System.Windows.Controls.ComboBox>(d) is not null))
            _dragRow = null;
    }

    private void ModsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        try { DragDrop.DoDragDrop(ModsGrid, row, DragDropEffects.Move); }
        finally { HideInsertion(); }
    }

    /// <summary>
    /// Shows where the drop will land. The line is clamped to the dragged row's band, so dragging a mod up past
    /// the game's modules parks the line at the top of the mods rather than refusing - you can see the limit
    /// instead of guessing at it.
    /// </summary>
    private void ModsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(ModRow)) is not ModRow dragged) { e.Effects = DragDropEffects.None; HideInsertion(); return; }
        e.Effects = DragDropEffects.Move;
        var at = InsertionIndex(dragged, e);
        ShowInsertionAt(at);
    }

    private void ModsGrid_Drop(object sender, DragEventArgs e)
    {
        HideInsertion();
        if (e.Data.GetData(typeof(ModRow)) is not ModRow dragged) return;
        var vm = ViewModel;
        if (vm.TryMoveTo(dragged, InsertionIndex(dragged, e))) vm.SelectedMod = dragged;
    }

    private void ModsGrid_DragLeave(object sender, DragEventArgs e) => HideInsertion();

    /// <summary>
    /// The gap the row would be inserted into, clamped to its band. Which gap depends on whether the pointer is in
    /// the upper or lower half of the row under it, so the line lands where the eye expects; past the last row it
    /// is the end of the list.
    /// </summary>
    private int InsertionIndex(ModRow dragged, DragEventArgs e)
    {
        var vm = ViewModel;
        var (bandStart, bandEnd) = vm.BandRange(dragged.Band);
        int at;
        if (RowUnder(e.OriginalSource as DependencyObject) is { Item: ModRow target } row)
        {
            var p = e.GetPosition(row);
            at = vm.Mods.IndexOf(target) + (p.Y > row.ActualHeight / 2 ? 1 : 0);
        }
        else
        {
            // Not over a row: below the last one means the end of the list, above the first means the start.
            var p = e.GetPosition(ModsGrid);
            at = p.Y <= 0 ? 0 : vm.Mods.Count;
        }
        return Math.Clamp(at, bandStart, bandEnd);
    }

    private void ShowInsertionAt(int index)
    {
        var layer = AdornerLayer.GetAdornerLayer(ModsGrid);
        if (layer is null) return;
        if (_insertion is null)
        {
            var brush = TryFindResource("LogTool") as Brush ?? SystemColors.HighlightBrush;
            _insertion = new InsertionAdorner(ModsGrid, brush);
            layer.Add(_insertion);
        }
        // The gap sits at the top of the row at `index`, or the bottom of the last row when it is past the end.
        double y;
        if (RowAt(index) is { } r) y = r.TranslatePoint(new Point(0, 0), ModsGrid).Y;
        else if (RowAt(index - 1) is { } prev) y = prev.TranslatePoint(new Point(0, prev.ActualHeight), ModsGrid).Y;
        else return;
        _insertion.MoveTo(y, ModsGrid.ActualWidth);
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(ModsGrid)?.Remove(_insertion);
        _insertion = null;
    }

    private System.Windows.Controls.DataGridRow? RowAt(int index)
    {
        if (index < 0 || index >= ModsGrid.Items.Count) return null;
        return ModsGrid.ItemContainerGenerator.ContainerFromIndex(index) as System.Windows.Controls.DataGridRow;
    }

    private static System.Windows.Controls.DataGridRow? RowUnder(DependencyObject? d) =>
        d is null ? null : FindAncestor<System.Windows.Controls.DataGridRow>(d);

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---- remembered UI state ----------------------------------------------------------------------------

    /// <summary>
    /// Restores the window where it was, but only when it would still be on a screen. A monitor unplugged since the
    /// last run is the case this guards: applying saved coordinates blindly puts the app somewhere invisible, and
    /// the user has no way to get it back.
    /// </summary>
    private void RestoreGeometry()
    {
        if (_ui.GeometryFitsIn(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight,
                               MinWidth, MinHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _ui.WindowLeft!.Value;
            Top = _ui.WindowTop!.Value;
            Width = _ui.WindowWidth!.Value;
            Height = _ui.WindowHeight!.Value;
            // GeometryFitsIn only guarantees the title bar is reachable. A saved size taller than the screen - a
            // different monitor, a changed resolution or scaling - otherwise pushes the status bar and the command
            // box off the bottom, with no sign anything is missing.
            var work = SystemParameters.WorkArea;
            if (new Rect(Left, Top, Width, Height).IntersectsWith(work))
            {
                if (Height > work.Height) Height = work.Height;
                if (Width > work.Width) Width = work.Width;
                if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
                if (Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            }
        }
        if (_ui.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveGeometry()
    {
        // RestoreBounds is the un-maximised rectangle; Left/Top/Width/Height read as the maximised one, which would
        // restore to a window that cannot be un-maximised back to anything sensible.
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _ui.WindowLeft = r.Left; _ui.WindowTop = r.Top;
            _ui.WindowWidth = r.Width; _ui.WindowHeight = r.Height;
        }
        _ui.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private void RestoreTheme()
    {
        _restoring = true;
        _theme = _ui.Theme == nameof(AppTheme.Dark) ? AppTheme.Dark : AppTheme.Light;
        App.ApplyTheme(_theme);
        ThemeButton.Content = _theme == AppTheme.Light ? "Dark" : "Light";
        _restoring = false;
    }

    /// <summary>
    /// Always opens on the first visible tab, which is Mods. Reopening on the last tab used meant a host who closed
    /// the app on Console started on an empty console, with the mod list a click away.
    /// </summary>
    private void RestoreSelectedTab()
    {
        _restoring = true;
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        _restoring = false;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || !ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedItem is not TabItem { Header: string header }) return;
        _ui.SelectedTab = header;
        UiStateStore.Save(_ui);
    }

    // ---- theme -----------------------------------------------------------------------------------------

    // The theme lives on the App's merged dictionaries, not on this window.
    private AppTheme _theme = AppTheme.Light;

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _theme = _theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        App.ApplyTheme(_theme);
        ThemeButton.Content = _theme == AppTheme.Light ? "Dark" : "Light";
        if (_restoring) return;
        _ui.Theme = _theme.ToString();
        UiStateStore.Save(_ui);
    }

    // ---- the rest --------------------------------------------------------------------------------------

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // A WPF Hyperlink raises this instead of navigating; without UseShellExecute nothing opens.
    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not open the link", MessageBoxButton.OK, MessageBoxImage.Warning); }
        e.Handled = true;
    }

    private void Profiles_DropDownOpened(object sender, System.EventArgs e) => ViewModel.RefreshProfileList();

    private void Command_KeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.Host is not { } host) return;
        if (e.Key == Key.Enter && host.SendCommandCommand.CanExecute(null)) host.SendCommandCommand.Execute(null);
    }

    /// <summary>
    /// A right-click does not select a DataGrid row by default, so the context menu would act on whatever row was
    /// selected before - which is rarely the one under the pointer.
    /// </summary>
    private void ModsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource as DependencyObject) is { Item: ModRow row }) ViewModel.SelectedMod = row;
    }

    private void ModsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel.SelectedMod is not { CanRemove: true }) return;
        ViewModel.RemoveModCommand.Execute(null);
        e.Handled = true;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveGeometry();
        UiStateStore.Save(_ui);
        // Unsaved list edits first: once the server question below is answered the window closes for real.
        if (!_closeConfirmed && !ViewModel.ResolveUnsavedChanges()) { e.Cancel = true; return; }
        if (_closeConfirmed || ViewModel.Host is not { IsRunning: true } host) return;
        CloseAfterStoppingServer(host, e);
    }

    private async void CloseAfterStoppingServer(HostViewModel host, CancelEventArgs e)
    {
        e.Cancel = true;
        if (MessageBox.Show("The server is running. Stop it and close?", "ModderLords", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await host.OnClosingAsync();
        _closeConfirmed = true;
        Close();
    }
}
