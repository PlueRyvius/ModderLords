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

    /// <summary>The Mods tab's "My order wins" checkbox binds to MainViewModel, not HostViewModel.</summary>
    [Fact]
    public void The_manual_order_checkbox_binds_to_a_settable_bool()
    {
        var p = typeof(MainViewModel).GetProperty("ManualLoadOrder");
        Assert.NotNull(p);
        Assert.Equal(typeof(bool), p!.PropertyType);
        Assert.True(p.CanRead && p.CanWrite);
    }

    /// <summary>
    /// The Server tab's toggles bind straight through to the profile. A flag with no way to set it in the app is a
    /// flag nobody can use: both of these existed as a profile property and a CLI switch before the UI caught up.
    /// </summary>
    [Theory]
    [InlineData("UseModDistanceCache", typeof(bool))]
    [InlineData("StallWarningSeconds", typeof(int?))]
    [InlineData("ManualLoadOrder", typeof(bool))]
    public void The_server_tab_toggles_bind_to_profile_properties(string name, Type expected)
    {
        var p = typeof(ModderLords.Core.Profiles.Profile).GetProperty(name);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.PropertyType);
        Assert.True(p.CanWrite);
    }

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
