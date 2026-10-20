using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        // Tunneling is required to observe plain Enter before the TextBox consumes it as a newline.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Compiled bindings cannot cast the window DataContext, so model choices are wired here.
        if (e.Root is Window { DataContext: WorkbenchViewModel workbench })
        {
            ModelSelector.ItemsSource = workbench.ModelChoices;
        }
    }

    private ConversationViewModel? ViewModel => DataContext as ConversationViewModel;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }
        e.Handled = true;
        if (ViewModel is { Composer.CanSend: true } viewModel)
        {
            _ = viewModel.SendAsync();
        }
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.SendAsync();

    private void OnStopClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.StopAsync();

    private void OnCompactClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.CompactAsync();

    private void OnModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<ModelChoice>().FirstOrDefault() is { } choice && ViewModel is { } viewModel)
        {
            _ = viewModel.SwitchModelAsync(viewModel.ApplyThoughtLevel(choice.Selection));
        }
    }
}
