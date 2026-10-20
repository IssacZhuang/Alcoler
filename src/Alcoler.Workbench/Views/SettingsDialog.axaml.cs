using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcoler.Workbench.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _isSaving;

    public SettingsDialog()
        : this(new WorkbenchConfig([]))
    {
    }

    internal SettingsDialog(WorkbenchConfig current)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(current);
        DataContext = _viewModel;
        ProvidersTab.IsChecked = true;
        Closing += (_, e) => e.Cancel = _isSaving;
        WindowChrome.Attach(this);
    }

    public static async Task<WorkbenchConfig?> ShowAsync(Window owner, WorkbenchConfig current)
    {
        var dialog = new SettingsDialog(current);
        return await dialog.ShowDialog<WorkbenchConfig?>(owner);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        _isSaving = true;
        SettingsContent.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        WorkbenchConfig? saved;
        try
        {
            saved = await _viewModel.SaveAsync();
        }
        finally
        {
            _isSaving = false;
            SettingsContent.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            BackButton.IsEnabled = true;
        }
        if (saved is not null)
        {
            Close(saved);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (!_isSaving)
        {
            Close(null);
        }
    }

    private void OnSectionChanged(object? sender, RoutedEventArgs e)
    {
        if (ProvidersPanel is null || sender is not RadioButton { IsChecked: true } tab)
        {
            return;
        }
        var section = tab.Tag as string;
        ProvidersPanel.IsVisible = section == "providers";
        McpPanel.IsVisible = section == "mcp";
        AppearancePanel.IsVisible = section == "appearance";
        SectionTitle.Text = section switch
        {
            "appearance" => "Appearance",
            "mcp" => "MCP servers",
            _ => "Model providers",
        };
    }
}
