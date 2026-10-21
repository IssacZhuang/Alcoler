using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _isSaving;

    public SettingsDialog()
        : this(new WorkbenchConfig([]))
    {
    }

    internal SettingsDialog(WorkbenchConfig current, Auth.ChatGptAccountService? chatGptAccount = null)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(current, chatGptAccount: chatGptAccount);
        DataContext = _viewModel;
        if (_viewModel.ChatGpt is { } chatGpt)
            chatGpt.OpenAuthorizeUrl = uri => Launcher.LaunchUriAsync(uri);
        ProvidersTab.IsChecked = true;
        Closing += (_, e) => e.Cancel = _isSaving;
        Closed += (_, _) => _viewModel.Dispose();
        WindowChrome.Attach(this);
    }

    public static async Task<WorkbenchConfig?> ShowAsync(Window owner, WorkbenchConfig current, Auth.ChatGptAccountService? chatGptAccount = null)
    {
        var dialog = new SettingsDialog(current, chatGptAccount);
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

    private void OnProviderAdded(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddProviderCommand.Execute(null);
        AddProviderButton.Flyout?.Hide();
    }

    private void OnAddTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderTemplate template })
        {
            _viewModel.AddProviderFromTemplate(template);
            AddProviderButton.Flyout?.Hide();
        }
    }

    private async void OnAddModelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderViewModel provider })
            await ModelConfigurationDialog.ShowAsync(this, provider);
    }

    private async void OnEditModelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModelViewModel { Provider: { } provider } model })
            await ModelConfigurationDialog.ShowAsync(this, provider, model);
    }

    private async void OnGetApiKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderViewModel { ApiKeyManagementUri: { } uri } })
        {
            try { await Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _viewModel.Error = $"Could not open API key management: {ex.Message}";
            }
        }
    }

    private void OnRenameProviderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProviderViewModel provider }) return;
        provider.RenameCommand.Execute(null);
        foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(button => ReferenceEquals(button.DataContext, provider)))
            button.Flyout?.Hide();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var input = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(box => ReferenceEquals(box.DataContext, provider) && box.Watermark == "Provider name");
            if (input is not null)
            {
                input.Focus();
                input.SelectAll();
            }
        });
    }

    private void OnProviderNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ProviderViewModel provider }) provider.FinishRenameCommand.Execute(null);
    }

    private void OnProviderNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox { DataContext: ProviderViewModel provider })
        {
            provider.FinishRenameCommand.Execute(null);
            e.Handled = true;
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
        RuntimePanel.IsVisible = section == "runtime";
        SectionTitle.Text = section switch
        {
            "runtime" => "Runtime",
            "appearance" => "Appearance",
            "mcp" => "MCP servers",
            _ => "Model providers",
        };
    }
}
