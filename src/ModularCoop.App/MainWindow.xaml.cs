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
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v is not null) Title += $"  v{v.Major}.{v.Minor}.{v.Build}";
        // Follow the tail only while the user is already at the bottom; scrolling up pins the view until they return.
        ViewModel.ConsoleFlushed += () =>
        {
            var sv = FindScrollViewer(ConsoleList);
            if (sv is null) return;
            var atBottom = sv.ScrollableHeight - sv.VerticalOffset < 3;
            if (ViewModel.AutoScroll && atBottom) sv.ScrollToEnd();
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


