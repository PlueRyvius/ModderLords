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
        ViewModel.Console.CollectionChanged += (_, _) =>
        {
            if (ConsoleList.Items.Count > 0) ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

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
