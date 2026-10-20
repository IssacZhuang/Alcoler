using Alcoler.Workbench.Ui;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcoler.Workbench.Views;

public sealed record NewSessionRequest(string WorkspaceRoot, string? Title);

public partial class NewSessionDialog : Window
{
    public NewSessionDialog()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        WorkspaceBox.Text = string.Empty;
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
            return;
        }
        var title = TitleBox.Text?.Trim();
        Close(new NewSessionRequest(workspaceRoot, string.IsNullOrEmpty(title) ? null : title));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
