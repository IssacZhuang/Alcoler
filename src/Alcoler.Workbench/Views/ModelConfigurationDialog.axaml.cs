using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ModelConfigurationDialog : Window
{
    private readonly ModelConfigurationViewModel _viewModel;

    public ModelConfigurationDialog()
        : this(new ProviderViewModel(_ => { }).CreateModelEditor())
    {
    }

    internal ModelConfigurationDialog(ModelConfigurationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            var input = viewModel.IsAdding ? ModelIdBox : ContextWindowBox;
            input.Focus();
            input.SelectAll();
        };
        WindowChrome.Attach(this);
    }

    public static Task<bool> ShowAsync(Window owner, ProviderViewModel provider, ModelViewModel? model = null) =>
        new ModelConfigurationDialog(provider.CreateModelEditor(model)).ShowDialog<bool>(owner);

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave()) Close(true);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
