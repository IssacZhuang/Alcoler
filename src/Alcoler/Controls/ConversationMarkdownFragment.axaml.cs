using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ColorDocument.Avalonia;
using Markdown.Avalonia;

namespace Alcoler.Controls;

/// <summary>Renders one stable markdown block; unlike <see cref="ConversationMarkdown"/> it owns no scroller.</summary>
public partial class ConversationMarkdownFragment : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ConversationMarkdownFragment, string?>(nameof(Markdown));

    private static ConversationMarkdownFragment? _activeSelection;
    private readonly IMarkdownEngine2 _engine;
    private DocumentElement? _document;
    private Point _selectionAnchor;
    private bool _selecting;
    private string? _renderedMarkdown;
    private bool _hasRenderedMarkdown;

    public ConversationMarkdownFragment()
    {
        InitializeComponent();
        _engine = ConversationMarkdownRendering.CreateEngine(trimDocumentEdges: false);
        _engine.Plugins = ConversationMarkdownRendering.CreatePlugins();
        _engine.CascadeResources.SetParent(this);
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, false);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownProperty)
            return;
        var markdown = Markdown;
        if (_hasRenderedMarkdown && string.Equals(_renderedMarkdown, markdown, StringComparison.Ordinal))
            return;
        _hasRenderedMarkdown = true;
        _renderedMarkdown = markdown;
        if (ReferenceEquals(_activeSelection, this))
            _activeSelection = null;
        _selecting = false;
        _document = _engine.TransformElement(markdown ?? string.Empty);
        var control = _document.Control;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Host.Content = control;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_document is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        ClearActiveSelection();
        _activeSelection = this;
        _selectionAnchor = e.GetPosition(_document.Control);
        _selecting = true;
        _document.Select(_selectionAnchor, _selectionAnchor);
        e.Pointer.Capture(this);
        Focus(NavigationMethod.Pointer);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_selecting || _document is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _document.Select(_selectionAnchor, e.GetPosition(_document.Control));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selecting || _document is null)
            return;
        _selecting = false;
        _document.Select(_selectionAnchor, e.GetPosition(_document.Control));
        e.Pointer.Capture(null);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _selecting = false;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || _document is null ||
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }
        var selected = _document.GetSelectedText();
        if (selected.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        e.Handled = true;
        try
        {
            await clipboard.SetTextAsync(selected);
        }
        catch (Exception)
        {
            // Clipboard access can fail transiently while the selection stays usable.
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(_activeSelection, this))
            _activeSelection = null;
        base.OnDetachedFromVisualTree(e);
    }

    internal static void ClearActiveSelection()
    {
        if (_activeSelection is not { } active)
            return;
        _activeSelection = null;
        active._selecting = false;
        active._document?.UnSelect();
    }
}
