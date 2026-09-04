using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ModularCoop.App.ViewModels;

namespace ModularCoop.App;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

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
        // Auto-scroll means auto-scroll: the tick follows the tail wherever you are, and unticking is how you stop
        // it to read something. It used to also require you to already be at the bottom, so scrolling up silently
        // disabled it and ticking the box while scrolled up appeared to do nothing at all.
        ViewModel.ConsoleFlushed += () =>
        {
            if (ViewModel.AutoScroll) FindScrollViewer(ConsoleList)?.ScrollToEnd();
        };
        // Ticking the box jumps to the bottom straight away, rather than waiting for the server to say something.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AutoScroll) && ViewModel.AutoScroll)
                Dispatcher.BeginInvoke(() => FindScrollViewer(ConsoleList)?.ScrollToEnd());
        };
    }

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

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // The theme lives on the App's merged dictionaries, not on this window; persisting the choice across
    // runs comes with the UI-state store.
    private AppTheme _theme = AppTheme.Light;

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _theme = _theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        App.ApplyTheme(_theme);
        ThemeButton.Content = _theme == AppTheme.Light ? "Dark" : "Light";
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
        if (e.Key == Key.Enter && ViewModel.SendCommandCommand.CanExecute(null)) ViewModel.SendCommandCommand.Execute(null);
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !ViewModel.IsRunning) return;
        e.Cancel = true;
        if (MessageBox.Show("The server is running. Stop it and close?", "Modular Bannerlords Coop", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await ViewModel.OnClosingAsync();
        _closeConfirmed = true;
        Close();
    }
}




