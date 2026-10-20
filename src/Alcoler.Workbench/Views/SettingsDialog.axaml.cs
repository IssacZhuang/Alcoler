using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcoler.Workbench.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsDialog()
        : this(new WorkbenchConfig([]))
    {
    }

    internal SettingsDialog(WorkbenchConfig current)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(current);
        DataContext = _viewModel;
        SectionList.SelectedIndex = 0;
        WindowChrome.Attach(this);
    }

    public static async Task<WorkbenchConfig?> ShowAsync(Window owner, WorkbenchConfig current)
    {
        var dialog = new SettingsDialog(current);
        return await dialog.ShowDialog<WorkbenchConfig?>(owner);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var saved = await _viewModel.SaveAsync();
        if (saved is not null)
        {
            Close(saved);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProvidersPanel is null)
        {
            return;
        }
        var index = SectionList.SelectedIndex;
        ProvidersPanel.IsVisible = index == 0;
        McpPanel.IsVisible = index == 1;
        AppearancePanel.IsVisible = index == 2;
    }
}
