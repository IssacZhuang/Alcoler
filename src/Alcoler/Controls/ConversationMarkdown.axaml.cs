using Avalonia;
using Avalonia.Controls;

namespace Alcoler.Controls;

public partial class ConversationMarkdown : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ConversationMarkdown, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<ConversationMarkdown, bool>(nameof(SelectionEnabled), true);

    public static readonly StyledProperty<bool> WrapCodeBlocksProperty =
        AvaloniaProperty.Register<ConversationMarkdown, bool>(nameof(WrapCodeBlocks));

    private string? _renderedMarkdown;
    private bool _hasRenderedMarkdown;

    public ConversationMarkdown()
    {
        InitializeComponent();
        ApplyRenderingMode();
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool SelectionEnabled
    {
        get => GetValue(SelectionEnabledProperty);
        set => SetValue(SelectionEnabledProperty, value);
    }

    public bool WrapCodeBlocks
    {
        get => GetValue(WrapCodeBlocksProperty);
        set => SetValue(WrapCodeBlocksProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            RenderMarkdown();
        else if (change.Property == SelectionEnabledProperty)
            Viewer.SelectionEnabled = SelectionEnabled;
        else if (change.Property == WrapCodeBlocksProperty)
            ApplyRenderingMode();
    }

    private void ApplyRenderingMode()
    {
        Viewer.Engine = ConversationMarkdownRendering.CreateEngine(wrapCodeBlocks: WrapCodeBlocks);
        Viewer.Plugins = ConversationMarkdownRendering.CreatePlugins();
        if (!_hasRenderedMarkdown)
            return;
        _hasRenderedMarkdown = false;
        RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        var markdown = Markdown;
        if (_hasRenderedMarkdown && string.Equals(_renderedMarkdown, markdown, StringComparison.Ordinal))
            return;
        _hasRenderedMarkdown = true;
        _renderedMarkdown = markdown;
        Viewer.Markdown = markdown;
    }
}
