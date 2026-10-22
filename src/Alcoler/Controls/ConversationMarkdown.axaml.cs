using Avalonia;
using Avalonia.Controls;

namespace Alcoler.Controls;

public partial class ConversationMarkdown : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ConversationMarkdown, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<ConversationMarkdown, bool>(nameof(SelectionEnabled), true);

    private string? _renderedMarkdown;
    private bool _hasRenderedMarkdown;

    public ConversationMarkdown()
    {
        InitializeComponent();
        Viewer.Engine = ConversationMarkdownRendering.CreateEngine();
        Viewer.Plugins = ConversationMarkdownRendering.CreatePlugins();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            var markdown = Markdown;
            if (!_hasRenderedMarkdown || !string.Equals(_renderedMarkdown, markdown, StringComparison.Ordinal))
            {
                _hasRenderedMarkdown = true;
                _renderedMarkdown = markdown;
                Viewer.Markdown = markdown;
            }
        }
        else if (change.Property == SelectionEnabledProperty)
            Viewer.SelectionEnabled = SelectionEnabled;
    }
}
