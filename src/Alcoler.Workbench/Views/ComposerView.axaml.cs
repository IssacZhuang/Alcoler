using System.ComponentModel;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ComposerView : UserControl
{
    private IComposerHost? _observed;
    private WorkbenchViewModel? _workbench;
    private Window? _owner;
    private readonly ProviderUsageViewModel _providerUsage = new();
    private readonly ProviderUsageView _providerUsageView;
    private readonly ComposerSkillCatalog _skills = new();
    private string? _dismissedSkillToken;
    private bool _recallingHistory;
    private string? _historyRecallText;
    private bool _pasting;

    public ComposerView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        _providerUsageView = new ProviderUsageView { DataContext = _providerUsage };
        ContextUsageHost.QuotaContent = _providerUsageView;
        ContextUsageHost.PopoverOpened += (_, _) => _ = _providerUsage.RefreshAsync();
        // Tunneling is required to observe plain Enter before the TextBox consumes it as a newline.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.PastingFromClipboard += OnPastingFromClipboard;
        InputBox.TextChanged += (_, _) => UpdateSkillSuggestions();
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty)
                UpdateSkillSuggestions();
        };
        SkillSuggestions.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is Control source
                && (source is ListBoxItem || source.FindAncestorOfType<ListBoxItem>() is not null))
                ApplySelectedSkill();
        };
        DataContextChanged += (_, _) => ObserveConversation();
        SizeChanged += (_, _) => Classes.Set("compact", Bounds.Width < 560);
        ModeSelector.Click += (_, _) => ClosePickers(ModelSelector, ThoughtLevelSelector);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Window owner)
        {
            if (_owner != owner)
            {
                if (_owner is not null)
                    _owner.Closed -= OnOwnerClosed;
                _owner = owner;
                owner.Closed += OnOwnerClosed;
            }
            _workbench = owner.DataContext as WorkbenchViewModel;
        }
        ObserveConversation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unobserve();
        _workbench = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        _providerUsage.Dispose();
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    private void Unobserve()
    {
        if (_observed is null)
            return;
        _observed.PropertyChanged -= OnConversationPropertyChanged;
        _observed.Composer.PropertyChanged -= OnComposerPropertyChanged;
        _observed = null;
    }

    private void ObserveConversation()
    {
        Unobserve();
        _observed = ViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnConversationPropertyChanged;
            _observed.Composer.PropertyChanged += OnComposerPropertyChanged;
            _observed.Composer.ConfigureWorkspace(_observed.WorkspaceRoot);
        }
        _dismissedSkillToken = null;
        _historyRecallText = null;
        SkillPanel.IsVisible = false;
        SynchronizeModel();
        BuildThoughtMenu();
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IComposerHost.ModelText))
            SynchronizeModel();
        if (e.PropertyName is nameof(IComposerHost.ThoughtLevels) or nameof(IComposerHost.ThoughtLevel))
            BuildThoughtMenu();
        if (e.PropertyName == nameof(IComposerHost.WorkspaceRoot) && ViewModel is { } host)
            host.Composer.ConfigureWorkspace(host.WorkspaceRoot);
    }

    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposerViewModel.Mode))
        {
            ModeSelector.Flyout?.Hide();
            InputBox.Focus();
        }
    }

    private void SynchronizeModel()
    {
        var choice = _workbench?.ModelChoices.FirstOrDefault(choice =>
            $"{choice.ProviderId}/{choice.ModelId}" == ViewModel?.ModelText);
        if (choice is not null)
            ViewModel?.ConfigureModel(choice);
        var provider = choice is not null ? _workbench?.FindProvider(choice.ProviderId) : null;
        ModelLabel.Text = choice is null ? ViewModel?.ModelText : $"{provider?.Label ?? choice.ProviderId}/{choice.ModelId}";
        _providerUsage.Configure(provider?.EffectiveId, provider?.BaseUrl, provider?.ApiKey);
        BuildModelMenu();
    }

    private IComposerHost? ViewModel => DataContext as IComposerHost;

    public void FocusInput() => InputBox.Focus();

    private static void ClosePickers(params Button[] buttons)
    {
        foreach (var button in buttons)
            button.Flyout?.Hide();
    }

    private void OnModelPickerClick(object? sender, RoutedEventArgs e)
    {
        ClosePickers(ModeSelector, ThoughtLevelSelector);
        BuildModelMenu();
    }

    private void BuildModelMenu()
    {
        if (ModelSelector.Flyout is not MenuFlyout menu)
            return;
        menu.Items.Clear();
        foreach (var group in (_workbench?.ModelChoices ?? []).GroupBy(choice => choice.ProviderId))
        {
            var provider = new MenuItem
            {
                Header = _workbench?.FindProvider(group.Key)?.Label ?? group.Key,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ViewModel?.CurrentSelection?.ProviderId == group.Key,
                MinWidth = 190,
            };
            foreach (var choice in group)
            {
                var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = choice.ModelId });
                if (choice.Properties?.SupportsImage == true)
                    header.Children.Add(new TextBlock { Text = "Vision", FontSize = 11, Opacity = .6 });
                var item = new MenuItem
                {
                    Header = header,
                    Tag = choice,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = ViewModel?.CurrentSelection is { } selected
                        && selected.ProviderId == choice.ProviderId && selected.ModelId == choice.ModelId,
                    MinWidth = 190,
                };
                item.Click += OnModelSelected;
                provider.Items.Add(item);
            }
            menu.Items.Add(provider);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage models", MinWidth = 190 };
        manage.Click += async (_, _) =>
        {
            menu.Hide();
            if (_owner?.GetVisualDescendants().OfType<SidebarView>().FirstOrDefault() is { } sidebar)
                await sidebar.ShowSettingsAsync();
        };
        menu.Items.Add(manage);
    }

    private void BuildThoughtMenu()
    {
        var levels = ViewModel?.ThoughtLevels ?? [];
        ThoughtChevron.IsVisible = levels.Count > 1;
        if (levels.Count <= 1)
        {
            ThoughtLevelSelector.Flyout?.Hide();
            ThoughtLevelSelector.Flyout = null;
            return;
        }
        if (ThoughtLevelSelector.Flyout is not MenuFlyout menu)
        {
            menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            ThoughtLevelSelector.Flyout = menu;
        }
        menu.Items.Clear();
        foreach (var level in levels)
        {
            var item = new MenuItem
            {
                Header = level,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = level == ViewModel?.ThoughtLevel,
                MinWidth = 130,
            };
            item.Click += (_, _) =>
            {
                if (ViewModel is { } host)
                    host.ThoughtLevel = level;
                menu.Hide();
                InputBox.Focus();
            };
            menu.Items.Add(item);
        }
    }

    private void OnThoughtPickerClick(object? sender, RoutedEventArgs e)
    {
        ClosePickers(ModeSelector, ModelSelector);
        if (ViewModel?.ThoughtLevels.Count <= 1)
            ThoughtLevelSelector.Flyout?.Hide();
    }

    private bool IsComposing => InputBox.GetVisualDescendants().OfType<TextPresenter>()
        .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsComposing)
            return;
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control && ViewModel is { } reasoningHost)
        {
            var levels = reasoningHost.ThoughtLevels;
            if (levels.Count > 0)
            {
                var index = levels.ToList().IndexOf(reasoningHost.ThoughtLevel);
                reasoningHost.ThoughtLevel = levels[(index + 1) % levels.Count];
                e.Handled = true;
            }
            return;
        }
        if (e.KeyModifiers == KeyModifiers.None && SkillPanel.IsVisible)
        {
            if (e.Key is Key.Up or Key.Down)
            {
                SkillSuggestions.SelectedIndex = Math.Clamp(SkillSuggestions.SelectedIndex + (e.Key == Key.Up ? -1 : 1),
                    0, SkillSuggestions.ItemCount - 1);
                SkillSuggestions.ScrollIntoView(SkillSuggestions.SelectedItem!);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Enter or Key.Tab)
            {
                ApplySelectedSkill();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                _dismissedSkillToken = ActiveSkillToken()?.Token;
                SkillPanel.IsVisible = false;
                e.Handled = true;
                return;
            }
        }
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down && ViewModel is { } historyHost)
        {
            _recallingHistory = true;
            try
            {
                if (historyHost.Composer.RecallHistory(e.Key == Key.Up))
                {
                    SkillPanel.IsVisible = false;
                    _historyRecallText = historyHost.Composer.Draft;
                    InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                    e.Handled = true;
                }
            }
            finally
            {
                _recallingHistory = false;
            }
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        e.Handled = true;
        if (ViewModel is { Composer.CanSend: true } viewModel)
            _ = viewModel.SendAsync();
    }

    private (int Start, int End, string Token)? ActiveSkillToken()
    {
        var text = InputBox.Text ?? string.Empty;
        if (InputBox.SelectionStart != InputBox.SelectionEnd)
            return null;
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        if (start == end || text[start] != '/' || caret <= start)
            return null;
        var token = text[start..end];
        return token[1..].IndexOfAny(['/', '@', '$', '#']) >= 0 ? null : (start, end, token);
    }

    private void UpdateSkillSuggestions()
    {
        if (_historyRecallText == InputBox.Text)
        {
            SkillPanel.IsVisible = false;
            return;
        }
        _historyRecallText = null;
        if (_recallingHistory || IsComposing || ViewModel is not { } host || string.IsNullOrWhiteSpace(host.WorkspaceRoot)
            || ActiveSkillToken() is not { } token || token.Token == _dismissedSkillToken)
        {
            SkillPanel.IsVisible = false;
            return;
        }
        _dismissedSkillToken = null;
        var entries = _skills.Query(host.WorkspaceRoot, token.Token);
        SkillSuggestions.ItemsSource = entries;
        SkillSuggestions.SelectedIndex = entries.Count > 0 ? 0 : -1;
        SkillPanel.IsVisible = entries.Count > 0;
    }

    private void ApplySelectedSkill()
    {
        if (ViewModel is not { } host || SkillSuggestions.SelectedItem is not ComposerSkillEntry skill
            || ActiveSkillToken() is not { } token)
            return;
        var text = InputBox.Text ?? string.Empty;
        var replacement = $"/{skill.Name} ";
        _dismissedSkillToken = $"/{skill.Name}";
        host.Composer.Draft = text[..token.Start] + replacement + text[token.End..];
        InputBox.CaretIndex = token.Start + replacement.Length;
        SkillPanel.IsVisible = false;
        InputBox.Focus();
    }

    private async void OnPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (_pasting || ViewModel is not { } host || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        e.Handled = true;
        _pasting = true;
        try
        {
            var images = await ComposerClipboardImages.ReadAsync(clipboard);
            if (images.Count > 0)
                host.Composer.AddAttachments(images);
            else if (await clipboard.GetTextAsync() is { } text && ReferenceEquals(host, ViewModel))
            {
                var current = InputBox.Text ?? string.Empty;
                var start = Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd);
                var end = Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd);
                host.Composer.Draft = current[..start] + text + current[end..];
                InputBox.CaretIndex = start + text.Length;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            host.Composer.InputError = $"Could not paste image. {exception.Message}";
        }
        finally
        {
            _pasting = false;
        }
    }

    private void OnRemoveImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerImageAttachment image })
            ViewModel?.Composer.RemoveAttachment(image);
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.SendAsync();

    private void OnStopClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.StopAsync();

    private void OnModelSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ModelChoice choice })
            SelectModel(choice);
    }

    internal void SelectModel(ModelChoice choice)
    {
        if (ViewModel is not { } host)
            return;
        host.ConfigureModel(choice, useDefaultReasoning: true);
        _ = host.SwitchModelAsync(host.ApplyThoughtLevel(choice.Selection));
        ModelSelector.Flyout?.Hide();
        InputBox.Focus();
    }
}
