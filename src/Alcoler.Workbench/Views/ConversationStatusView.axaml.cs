using System.ComponentModel;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ConversationStatusView : UserControl
{
    public static readonly StyledProperty<double> ConversationWidthProperty =
        AvaloniaProperty.Register<ConversationStatusView, double>(nameof(ConversationWidth));

    public static readonly StyledProperty<bool?> ExpandedOverrideProperty =
        AvaloniaProperty.Register<ConversationStatusView, bool?>(nameof(ExpandedOverride));

    public static readonly DirectProperty<ConversationStatusView, bool> IsExpandedProperty =
        AvaloniaProperty.RegisterDirect<ConversationStatusView, bool>(nameof(IsExpanded), view => view.IsExpanded);

    private readonly DispatcherTimer _activityTimer;
    private readonly List<Visual> _visibilityAncestors = [];
    private ConversationStatusViewModel? _observedViewModel;
    private bool _initialized;
    private bool _attached;
    private bool _isExpanded;
    private int _terminalCount;
    private int _agentCount;

    public ConversationStatusView()
    {
        InitializeComponent(true);
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityTimer.Tick += OnActivityTick;
        _initialized = true;
        UpdateContentState();
    }

    public event Action<StatusActivity>? ActivityRequested;

    public event Action<StatusPlan>? PlanRequested;

    public event Action? ReviewRequested;

    public event EventHandler? DisplayModeChanged;

    public double ConversationWidth
    {
        get => GetValue(ConversationWidthProperty);
        set => SetValue(ConversationWidthProperty, value);
    }

    public bool? ExpandedOverride
    {
        get => GetValue(ExpandedOverrideProperty);
        set => SetValue(ExpandedOverrideProperty, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetAndRaise(IsExpandedProperty, ref _isExpanded, value);
    }

    private ConversationStatusViewModel? ViewModel => DataContext as ConversationStatusViewModel;

    public void SetConversationWidth(double width) => SetCurrentValue(ConversationWidthProperty, width);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_initialized)
        {
            return;
        }
        if (change.Property == ConversationWidthProperty || change.Property == ExpandedOverrideProperty)
        {
            UpdateDisplayMode();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (!_initialized)
        {
            return;
        }
        ObserveViewModel();
        HideFlyouts();
        GitSection.IsExpanded = true;
        PlansSection.IsExpanded = true;
        ProgressSection.IsExpanded = true;
        TerminalsSection.IsExpanded = false;
        AgentsSection.IsExpanded = false;
        _terminalCount = 0;
        _agentCount = 0;
        SetCurrentValue(ExpandedOverrideProperty, null);
        StatusScroll.Offset = default;
        UpdateContentState();
        RefreshVisibleActivities();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ObserveViewModel();
        foreach (var visual in this.GetVisualAncestors().Prepend(this))
        {
            _visibilityAncestors.Add(visual);
            visual.PropertyChanged += OnVisibilityChanged;
        }
        UpdateContentState();
        RefreshVisibleActivities();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _activityTimer.Stop();
        ObserveViewModel();
        HideFlyouts();
        foreach (var visual in _visibilityAncestors)
        {
            visual.PropertyChanged -= OnVisibilityChanged;
        }
        _visibilityAncestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveViewModel()
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _observedViewModel = _attached ? ViewModel : null;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateContentState();

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateActivityTimer();
            if (_activityTimer.IsEnabled)
            {
                RefreshVisibleActivities();
            }
        }
    }

    private void UpdateContentState()
    {
        var viewModel = ViewModel;
        var terminalCount = viewModel?.Terminals.Count ?? 0;
        var agentCount = viewModel?.Agents.Count ?? 0;
        if (_terminalCount > 0 && terminalCount == 0)
        {
            TerminalsSection.IsExpanded = false;
        }
        if (_agentCount > 0 && agentCount == 0)
        {
            AgentsSection.IsExpanded = false;
        }
        _terminalCount = terminalCount;
        _agentCount = agentCount;

        var preceded = false;
        SetSectionSeparator(GitContainer, viewModel?.HasGit == true, ref preceded);
        SetSectionSeparator(PlansContainer, viewModel?.HasPlans == true, ref preceded);
        SetSectionSeparator(ProgressContainer, viewModel?.HasTodos == true, ref preceded);
        SetSectionSeparator(TerminalsContainer, viewModel?.HasTerminals == true, ref preceded);
        SetSectionSeparator(AgentsContainer, viewModel?.HasAgents == true, ref preceded);
        EndedAgentsContainer.BorderThickness = agentCount > 0 ? new Thickness(0, 1, 0, 0) : default;
        EndedAgentsContainer.Padding = agentCount > 0 ? new Thickness(0, 8, 0, 0) : default;
        UpdateDisplayMode();
        UpdateActivityTimer();
    }

    private static void SetSectionSeparator(Border container, bool visible, ref bool preceded)
    {
        container.BorderThickness = visible && preceded ? new Thickness(0, 1, 0, 0) : default;
        container.Padding = visible && preceded ? new Thickness(0, 8, 0, 0) : default;
        preceded |= visible;
    }

    private void UpdateDisplayMode()
    {
        var expanded = ViewModel?.HasContent == true && (ExpandedOverride ?? ConversationWidth >= 1280);
        var changed = IsExpanded != expanded;
        IsExpanded = expanded;
        StatusShell.MaxWidth = ConversationWidth > 0 ? Math.Clamp(ConversationWidth - 32, 0, 320) : 320;
        StatusShell.Width = expanded ? StatusShell.MaxWidth : double.NaN;
        if (changed)
        {
            DisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateActivityTimer()
    {
        var shouldTick = _attached && IsEffectivelyVisible && ViewModel?.HasContent == true
            && (_terminalCount > 0 || _agentCount > 0);
        if (shouldTick && !_activityTimer.IsEnabled)
        {
            _activityTimer.Start();
        }
        else if (!shouldTick)
        {
            _activityTimer.Stop();
        }
    }

    private void RefreshVisibleActivities()
    {
        if (_attached && IsEffectivelyVisible && ViewModel?.HasContent == true)
        {
            ViewModel.RefreshActivities();
        }
    }

    private void OnActivityTick(object? sender, EventArgs e)
    {
        UpdateActivityTimer();
        if (_activityTimer.IsEnabled)
        {
            ViewModel?.RefreshActivities();
        }
    }

    private void OnExpandClick(object? sender, RoutedEventArgs e)
    {
        SetCurrentValue(ExpandedOverrideProperty, true);
        e.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        HideFlyouts();
        SetCurrentValue(ExpandedOverrideProperty, false);
        e.Handled = true;
    }

    private void OnAutoExpandClick(object? sender, RoutedEventArgs e)
    {
        DisplayModeButton.Flyout?.Hide();
        SetCurrentValue(ExpandedOverrideProperty, null);
        e.Handled = true;
    }

    private void OnReviewClick(object? sender, RoutedEventArgs e)
    {
        ReviewRequested?.Invoke();
        e.Handled = true;
    }

    private void OnPlanClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StatusPlan plan })
        {
            PlanRequested?.Invoke(plan);
            e.Handled = true;
        }
    }

    private void OnActivityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StatusActivity activity })
        {
            EndedAgentsButton.Flyout?.Hide();
            ActivityRequested?.Invoke(activity);
            e.Handled = true;
        }
    }

    private void OnStopActivityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StatusActivity { CanStop: true } activity })
        {
            ViewModel?.StopActivity(activity);
            e.Handled = true;
        }
    }

    private void OnBranchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitBranchViewModel { IsCurrent: false } branch }
            && ViewModel?.Git is { } git && git.CheckoutCommand.CanExecute(branch.Name))
        {
            BranchButton.Flyout?.Hide();
            git.CheckoutCommand.Execute(branch.Name);
            e.Handled = true;
        }
    }

    private void HideFlyouts()
    {
        DisplayModeButton.Flyout?.Hide();
        BranchButton.Flyout?.Hide();
        EarlierTodosButton.Flyout?.Hide();
        LaterTodosButton.Flyout?.Hide();
        EndedAgentsButton.Flyout?.Hide();
    }
}
