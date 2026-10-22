using Alcoler.Ui;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Alcoler.Views;

public sealed record NewSessionRequest(string WorkspaceRoot, string? Title);

public partial class NewSessionDialog : Window
{
    public NewSessionDialog()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        WorkspaceBox.Text = string.Empty;
        Opened += (_, _) =>
        {
            BrowseButton.IsVisible = StorageProvider.CanPickFolder;
            if (string.IsNullOrWhiteSpace(WorkspaceBox.Text))
            {
                WorkspaceBox.Focus();
            }
            else
            {
                TitleBox.Focus();
            }
        };
        WindowChrome.Attach(this);
    }

    private NewSessionDialog(string defaultWorkspaceRoot) : this()
    {
        WorkspaceBox.Text = defaultWorkspaceRoot;
    }

    public static async Task<NewSessionRequest?> ShowAsync(Window owner, string defaultWorkspaceRoot)
    {
        var dialog = new NewSessionDialog(defaultWorkspaceRoot);
        return await dialog.ShowDialog<NewSessionRequest?>(owner);
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var workspaceRoot = WorkspaceBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            WorkspaceError.IsVisible = true;
            WorkspaceBox.Focus();
            return;
        }
        var title = TitleBox.Text?.Trim();
        Close(new NewSessionRequest(workspaceRoot, string.IsNullOrEmpty(title) ? null : title));
    }

    private void OnWorkspaceChanged(object? sender, TextChangedEventArgs e)
    {
        if (WorkspaceError is not null)
        {
            WorkspaceError.IsVisible = false;
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose workspace folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            WorkspaceBox.Text = path;
            TitleBox.Focus();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
