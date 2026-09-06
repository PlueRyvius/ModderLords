using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        if (mode is null)
        {
            var dlg = new FirstRunWindow { Owner = null };
            dlg.ShowDialog();
            mode = dlg.ChosenMode;
            _ui.Mode = mode;
            UiStateStore.Save(_ui);
        }
        ViewModel.ApplyMode(mode.Value);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        AttachHost();
        UpdateModeButton();
        RestoreSelectedTab();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ---- mode ------------------------------------------------------------------------------------------

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Host)) AttachHost();
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
    /// One click, no restart - Andy asked for exactly that. Switching to Host builds the host view model the first
    /// time; switching back to Player only hides it, because a running server must survive the trip.
    /// </summary>
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Mode == AppMode.Host && ViewModel.Host is { IsRunning: true })
        {
            MessageBox.Show("Stop the server before switching to Player mode.", "ModderLords",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ViewModel.Mode = ViewModel.Mode == AppMode.Player ? AppMode.Host : AppMode.Player;
    }

    private void UpdateModeButton()
    {
        var host = ViewModel.Mode == AppMode.Host;
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

    private void RestoreSelectedTab()
    {
        _restoring = true;
        var wanted = Tabs.Items.OfType<TabItem>()
            .FirstOrDefault(t => t.Visibility == Visibility.Visible && (t.Header as string) == _ui.SelectedTab);
        Tabs.SelectedItem = wanted ?? Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
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

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveGeometry();
        UiStateStore.Save(_ui);
        if (_closeConfirmed || ViewModel.Host is not { IsRunning: true } host) return;
        e.Cancel = true;
        if (MessageBox.Show("The server is running. Stop it and close?", "ModderLords", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await host.OnClosingAsync();
        _closeConfirmed = true;
        Close();
    }
}
