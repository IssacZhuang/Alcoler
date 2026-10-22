using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Markdown.Avalonia;

namespace Alcoler.Controls;

/// <summary>Renders one stable markdown block; unlike <see cref="ConversationMarkdown"/> it owns no scroller or selection helper.</summary>
public partial class ConversationMarkdownFragment : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ConversationMarkdownFragment, string?>(nameof(Markdown));

    private readonly IMarkdownEngine2 _engine;
    private string? _renderedMarkdown;
    private bool _hasRenderedMarkdown;

    public ConversationMarkdownFragment()
    {
        InitializeComponent();
        _engine = ConversationMarkdownRendering.CreateEngine(trimDocumentEdges: false);
        _engine.Plugins = ConversationMarkdownRendering.CreatePlugins();
        _engine.CascadeResources.SetParent(this);
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
        var control = _engine.TransformElement(markdown ?? string.Empty).Control;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Host.Content = control;
    }
}
