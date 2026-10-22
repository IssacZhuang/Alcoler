using Alcoler.Ui;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcoler.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent(true);
    }

    internal ConfirmDialog(string message, string confirmLabel) : this()
    {
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        WindowChrome.Attach(this);
    }

    public static Task<bool> ShowAsync(Window owner, string message, string confirmLabel)
    {
        var dialog = new ConfirmDialog(message, confirmLabel);
        return dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
