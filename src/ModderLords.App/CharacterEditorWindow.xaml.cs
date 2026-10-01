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
        Loaded += async (_, _) => await vm.ReloadCommand.ExecuteAsync(null);
    }
}
