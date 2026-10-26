using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Alcoler.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace Alcoler.Controls;

internal static class ConversationMarkdownRendering
{
    private static readonly ConcurrentDictionary<(char Delimiter, int Length), Regex> ClosingFences = new();

    public static IMarkdownEngine2 CreateEngine(bool trimDocumentEdges = true, bool wrapCodeBlocks = false) =>
        new ConversationEngine { TrimDocumentEdges = trimDocumentEdges, WrapCodeBlocks = wrapCodeBlocks };

    public static MdAvPlugins CreatePlugins()
    {
        var plugins = new MdAvPlugins();
        plugins.Plugins.Add(new ChatAISetup());
        plugins.Plugins.Add(new ConversationPlugin());
        return plugins;
    }

    private sealed class ConversationEngine : global::Markdown.Avalonia.Markdown, IMarkdownEngine2
    {
        public bool TrimDocumentEdges { get; set; } = true;
        public bool WrapCodeBlocks { get; set; }

        DocumentElement IMarkdownEngine2.TransformElement(string text)
        {
            var document = base.TransformElement(text);
            Decorate(document, TrimDocumentEdges);
            foreach (var strong in document.Control.GetLogicalDescendants().OfType<CBold>())
                strong.FontWeight = FontWeight.Medium;
            return document;
        }

        private static void Decorate(DocumentElement element, bool trimDocumentEdges, bool nestedList = false)
        {
            var children = element.Children.ToArray();
            foreach (var child in children)
                Decorate(child, trimDocumentEdges, nestedList || element is ListBlockElement);

            switch (element)
            {
                case CTextBlockElement when element.Control is CTextBlock text
                    && text.GetLogicalDescendants().OfType<CImage>().Any():
                    text.LineHeight = double.NaN;
                    break;
                case ListBlockElement when element.Control is Grid list:
                    list.Margin = new Thickness(20, nestedList ? 6 : 12, 0, nestedList ? 6 : 12);
                    foreach (var marker in list.Children.OfType<CTextBlock>().ToArray())
                    {
                        if (marker.Text is not ("○" or "▪" or "❏"))
                            continue;
                        // Native list construction caches CTextBlock.Text before this decoration pass.
                        var filled = new CTextBlock("•") { TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.NoWrap };
                        filled.Classes.Add("ListMarker");
                        var row = Grid.GetRow(marker);
                        Grid.SetRow(filled, row);
                        Grid.SetColumn(filled, 0);
                        var item = list.Children.First(child => Grid.GetRow(child) == row && Grid.GetColumn(child) == 1);
                        var firstText = item.GetLogicalDescendants().OfType<CTextBlock>().FirstOrDefault();
                        if (firstText is not null)
                            filled.ObserveBaseHeightOf(firstText);
                        list.Children[list.Children.IndexOf(marker)] = filled;
                    }
                    foreach (var item in children)
                        item.Control.Margin = new Thickness(4, 0, 0, 0);
                    break;
                case TableBlockElement when element.Control is Border { Child: Grid table } border:
                    border.Child = null;
                    foreach (var column in table.ColumnDefinitions)
                        column.Width = new GridLength(1, GridUnitType.Star);
                    var scroller = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = table,
                    };
                    table.Bind(Layoutable.MinWidthProperty, new Binding("Viewport.Width") { Source = scroller });
                    scroller.Classes.Add("markdownTableScroll");
                    border.Child = scroller;
                    foreach (var block in table.GetLogicalDescendants().OfType<CTextBlock>())
                        block.TextWrapping = TextWrapping.NoWrap;
                    break;
            }

            if (element is DocumentRootElement or BlockquoteElement or ListItemElement)
            {
                SetBlockMargins(children, element is BlockquoteElement, element is ListItemElement,
                    trimDocumentEdges || element is not DocumentRootElement);
            }
        }

        private static void SetBlockMargins(DocumentElement[] children, bool quote, bool listItem, bool trimEdges)
        {
            Control? previous = null;
            for (var index = 0; index < children.Length; index++)
            {
                var control = children[index].Control;
                var heading = control.Classes.Any(name => name.StartsWith("Heading", StringComparison.Ordinal));
                var margin = children[index] switch
                {
                    ListBlockElement => control.Margin,
                    BlockquoteElement or TableBlockElement => new Thickness(0, 16, 0, 16),
                    _ when control.Classes.Contains("CodeBlock") => new Thickness(0, 16, 0, 16),
                    _ when heading => new Thickness(0, 24, 0, 16),
                    _ => new Thickness(0, 0, 0, 16),
                };
                if (quote || listItem)
                    margin = new Thickness(margin.Left, index == 0 ? 0 : children[index] is ListBlockElement ? 6 : quote ? 8 : 0, margin.Right, 0);
                if (trimEdges && index == 0)
                    margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
                if (trimEdges && index == children.Length - 1)
                    margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
                if (previous is not null && margin.Top > 0)
                {
                    var prior = previous.Margin;
                    margin = new Thickness(margin.Left, Math.Max(margin.Top, prior.Bottom), margin.Right, margin.Bottom);
                    previous.Margin = new Thickness(prior.Left, prior.Top, prior.Right, 0);
                }
                control.Margin = margin;
                previous = control;
            }
        }
    }

    private sealed class ConversationPlugin : IMdAvPlugin
    {
        public void Setup(SetupInfo info)
        {
            info.Register(new FenceOverride());
            info.RegisterTop(new TildeFenceParser());
        }
    }

    private sealed class FenceOverride() : BlockOverride2("CodeBlocksWithLangEvaluator")
    {
        public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status,
            IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd) =>
            ConvertFence(text, firstMatch, engine is ConversationEngine { WrapCodeBlocks: true },
                out parseTextBegin, out parseTextEnd);
    }

    private sealed class TildeFenceParser() : BlockParser2(
        new Regex(@"^[ ]{0,3}(~{3,})([^\n]*)\n", RegexOptions.Multiline | RegexOptions.Compiled), "ConversationTildeFence")
    {
        public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status,
            IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd) =>
            ConvertFence(text, firstMatch, engine is ConversationEngine { WrapCodeBlocks: true },
                out parseTextBegin, out parseTextEnd);
    }

    private static IEnumerable<DocumentElement> ConvertFence(
        string text, Match match, bool wrapCodeBlocks, out int begin, out int end)
    {
        var delimiter = match.Groups[1].Value;
        var contentStart = match.Index + match.Length;
        var closingRegex = ClosingFences.GetOrAdd((delimiter[0], delimiter.Length),
            key => new Regex(@"^[ ]{0,3}" + Regex.Escape(key.Delimiter.ToString()) + "{" + key.Length + @",}[ \t]*(?:\n|$)",
                RegexOptions.Multiline));
        var closing = closingRegex.Match(text, contentStart);
        begin = match.Index;
        end = closing.Success ? closing.Index + closing.Length : text.Length;
        var code = text[contentStart..(closing.Success ? closing.Index : text.Length)];
        var language = match.Groups[2].Value.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "text";
        var block = new CTextBlockElement([new CRun { Text = code.TrimEnd('\n') }], "CodeBlock");
        var textBlock = (CTextBlock)block.Control;
        textBlock.TextWrapping = wrapCodeBlocks ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var copy = new Button { Content = Loc.Get("markdown_copy") };
        copy.Classes.Add("copyCode");
        AutomationProperties.SetName(copy, Loc.Get("markdown_copy_code"));
        ToolTip.SetTip(copy, Loc.Get("markdown_copy_code"));
        copy.Click += async (_, e) =>
        {
            e.Handled = true;
            if (TopLevel.GetTopLevel(copy)?.Clipboard is not { } clipboard)
                return;
            try
            {
                await clipboard.SetTextAsync(code);
                copy.Content = Loc.Get("markdown_copied");
                DispatcherTimer.RunOnce(() => copy.Content = Loc.Get("markdown_copy"), TimeSpan.FromSeconds(1.2));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                copy.Content = Loc.Get("markdown_copy_failed");
            }
        };
        var label = new TextBlock { Text = language, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("codeLanguage");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Classes.Add("codeHeader");
        header.Children.Add(label);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = wrapCodeBlocks
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = textBlock,
        };
        scroll.Classes.Add("CodeBlock");
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(scroll);
        var border = new Border { Child = panel };
        border.Classes.Add("CodeBlock");
        return [new CodeFenceElement(border, block)];
    }

    private sealed class CodeFenceElement(Control control, CTextBlockElement text) : DocumentElement
    {
        public override Control Control => control;
        public override IEnumerable<DocumentElement> Children => [text];

        public override void Select(Point from, Point to)
        {
            var origin = text.Control.TranslatePoint(default, control) ?? default;
            text.Select(new Point(from.X - origin.X, from.Y - origin.Y), new Point(to.X - origin.X, to.Y - origin.Y));
        }

        public override void UnSelect() => text.UnSelect();

        public override void ConstructSelectedText(System.Text.StringBuilder builder) => text.ConstructSelectedText(builder);
    }
}
