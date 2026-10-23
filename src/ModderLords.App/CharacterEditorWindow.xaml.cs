using System.Windows;
using ModderLords.App.ViewModels;

namespace ModderLords.App;

/// <summary>Edits one player's hero on the running server. All the work is <see cref="CharacterEditorViewModel"/>.</summary>
public partial class CharacterEditorWindow : Window
{
    public CharacterEditorWindow(CharacterEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            vm.LoadLibrary();
            await vm.ReloadCommand.ExecuteAsync(null);
        };
        // Another editor window may have saved a face meanwhile.
        Activated += (_, _) => vm.LoadLibrary();
    }

    // The window is shown modeless, where IsCancel alone does not close it.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
