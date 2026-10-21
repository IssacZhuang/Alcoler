using System.ComponentModel;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class QuestionCardView : UserControl
{
    private PendingInteractionViewModel? _observed;

    public QuestionCardView()
    {
        InitializeComponent(true);
        QuestionCard.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        QuestionCard.AddHandler(KeyUpEvent, OnCardKeyUp, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => ObserveInteraction();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 560;
            QuestionFooter.RowSpacing = compact ? 8 : 0;
            Grid.SetColumnSpan(KeyboardHint, compact ? 2 : 1);
            Grid.SetRow(QuestionActions, compact ? 1 : 0);
        };
    }

    public event EventHandler? SubmitRequested;
    public event EventHandler? DismissRequested;

    private PendingInteractionViewModel? ViewModel => DataContext as PendingInteractionViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveInteraction();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observed is not null)
            _observed.PropertyChanged -= OnInteractionPropertyChanged;
        _observed = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveInteraction()
    {
        if (_observed is not null)
            _observed.PropertyChanged -= OnInteractionPropertyChanged;
        _observed = ViewModel;
        if (_observed is not null)
            _observed.PropertyChanged += OnInteractionPropertyChanged;
        UpdatePreviewLayout();
        QueueFocus();
    }

    private void OnInteractionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PendingInteractionViewModel.CurrentQuestion) or nameof(PendingInteractionViewModel.ActiveOptionIndex))
            QueueFocus();
        if (e.PropertyName is nameof(PendingInteractionViewModel.HasPreviews))
            UpdatePreviewLayout();
    }

    private void UpdatePreviewLayout()
    {
        var previews = ViewModel?.HasPreviews == true;
        QuestionChoices.ColumnDefinitions[1].Width = new GridLength(previews ? 1 : 0, GridUnitType.Star);
        QuestionChoices.ColumnSpacing = previews ? 12 : 0;
    }

    private void QueueFocus() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsVisible || !IsEnabled || ViewModel is not { IsPermission: false } pending
            || TopLevel.GetTopLevel(this) is null)
            return;
        if (pending.CurrentQuestion is { } question && pending.ActiveOptionIndex == question.Options.Count)
        {
            CustomAnswerInput.Focus();
            CustomAnswerRow.BringIntoView();
        }
        else if (pending.ActiveOptionIndex >= 0 && QuestionOptions.GetVisualDescendants().OfType<Button>()
                     .FirstOrDefault(button => button.Tag is QuestionOptionViewModel option && option.Index == pending.ActiveOptionIndex) is { } option)
        {
            option.Focus();
            option.BringIntoView();
        }
        else
        {
            QuestionCard.Focus();
            QuestionBody.Offset = default;
        }
    }, DispatcherPriority.Loaded);

    private void OnOptionGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is Button { Tag: QuestionOptionViewModel option } && ViewModel is { } pending)
            pending.ActiveOptionIndex = option.Index;
    }

    private void OnCustomGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (ViewModel is { CurrentQuestion: { } question } pending)
            pending.ActiveOptionIndex = question.Options.Count;
    }

    private void OnCustomPointerPressed(object? sender, PointerPressedEventArgs e) => CustomAnswerInput.Focus();

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuestionOptionViewModel option } && ViewModel?.ActivateOption(option.Index) == true)
            SubmitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnContinueClick(object? sender, RoutedEventArgs e) => Continue();
    private void OnDismissClick(object? sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);
    private void OnPreviousClick(object? sender, RoutedEventArgs e) => ViewModel?.MovePrevious();
    private void OnNextClick(object? sender, RoutedEventArgs e) => ViewModel?.MoveNext();

    private void Continue()
    {
        if (ViewModel?.ContinueQuestion() == true)
            SubmitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void GoBackOrDismiss()
    {
        if (ViewModel is { CanMovePrevious: true } pending)
            pending.MovePrevious();
        else
            DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCardKeyUp(object? sender, KeyEventArgs e)
    {
        // Button activates Space on key release even when the card handled key press.
        if (e.Key == Key.Space && e.Source is Visual source
            && source.GetSelfAndVisualAncestors().OfType<Button>().Any(button => button.Tag is QuestionOptionViewModel))
            e.Handled = true;
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } pending)
            return;
        var customFocused = CustomAnswerInput.IsKeyboardFocusWithin;
        if (customFocused && CustomAnswerInput.GetVisualDescendants().OfType<TextPresenter>()
                .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)))
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            GoBackOrDismiss();
            return;
        }
        if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            SubmitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (PreviewNotesInput.IsKeyboardFocusWithin)
            return;
        if (customFocused)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Continue();
            }
            else if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                pending.MoveSelection(e.Key == Key.Up ? -1 : 1);
            }
            return;
        }
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>()
                .FirstOrDefault() is { Tag: not QuestionOptionViewModel })
            return;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Left:
                e.Handled = true;
                pending.MoveSelection(-1);
                break;
            case Key.Down:
            case Key.Right:
            case Key.Tab:
                e.Handled = true;
                pending.MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (pending.ActiveOptionIndex < 0 || pending.CurrentQuestion?.MultiSelect == true)
                    Continue();
                else if (pending.ActivateOption(pending.ActiveOptionIndex))
                    SubmitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Space:
                e.Handled = true;
                if (pending.ActivateOption(pending.ActiveOptionIndex))
                    SubmitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
