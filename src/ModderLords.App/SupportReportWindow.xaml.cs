using System.Windows;

namespace ModderLords.App;

public partial class SupportReportWindow : Window
{
    public SupportSaveChoice SelectedSave => SaveBox.SelectedItem as SupportSaveChoice
        ?? new SupportSaveChoice("None", null);

    internal SupportReportWindow(SupportReportContext context)
    {
        InitializeComponent();
        CategoriesGrid.ItemsSource = context.Categories;
        SaveBox.ItemsSource = context.SaveChoices;
        SaveBox.SelectedIndex = 0;
        SaveBox.IsEnabled = !context.ServerRunning && context.SaveChoices.Count > 1;
        SaveNote.Text = context.ServerRunning
            ? "Stop the server before including a save; a live save may be changing. Safe save metadata is still included."
            : context.SaveChoices.Count > 1
                ? "No save is included by default. Choose one only when reproducing the problem requires campaign state."
                : "No server save was found. Safe metadata will be included when one is available.";
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
