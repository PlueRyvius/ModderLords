using ModderLords.App.ViewModels;
using Xunit;

namespace ModderLords.App.Tests;

/// <summary>
/// The Saves tab's import section binds to members by name, and WPF fails those bindings silently at runtime — a
/// renamed command shows an empty grid and a dead button rather than an error. These assert the names the XAML uses.
/// </summary>
public class SaveImportBindingTests
{
    private static readonly Type Vm = typeof(HostViewModel);

    [Theory]
    [InlineData("ClientSaves")]           // <DataGrid ItemsSource="{Binding ClientSaves}">
    [InlineData("SelectedClientSave")]    //           SelectedItem="{Binding SelectedClientSave}"
    [InlineData("Saves")]
    [InlineData("SelectedSave")]
    [InlineData("SaveDiff")]
    public void The_saves_tab_binds_to_properties_that_exist(string name)
        => Assert.NotNull(Vm.GetProperty(name));

    [Theory]
    // CommunityToolkit generates these from [RelayCommand] methods; the XAML names the generated command.
    [InlineData("RefreshClientSavesCommand")]
    [InlineData("ImportClientSaveCommand")]
    public void The_import_buttons_bind_to_commands_that_exist(string name)
    {
        var p = Vm.GetProperty(name);
        Assert.NotNull(p);
        Assert.True(typeof(System.Windows.Input.ICommand).IsAssignableFrom(p!.PropertyType), name + " is not an ICommand");
    }
}
