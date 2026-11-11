using System.ComponentModel;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Controls;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;
using Markdown.Avalonia.Controls;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Controls;

public class ConversationMarkdownTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypographyAndInlineSurfaces_ResolveInBothThemes(bool fragment)
    {
        const string markdown = "# One\n\n## Two\n\n### Three\n\n#### Four\n\n##### Five\n\n###### Six\n\nBody **strong** with `inline` and [link](https://example.com).";
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = markdown }
            : new ConversationMarkdown { Markdown = markdown };
        using var host = new Harness(view);
        var blocks = view.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        var body = Assert.Single(blocks, block => block.Classes.Contains("Paragraph"));
        Assert.Equal(14, body.FontSize);
        Assert.Equal("JetBrains Mono", body.FontFamily.Name);
        Assert.Equal(24.5, body.LineHeight);
        Assert.Equal(fragment ? 24 : 0, blocks[0].Margin.Top);
        Assert.Equal(fragment ? 16 : 0, body.Margin.Bottom);
        var sizes = new double[] { 18, 16, 14, 14, 14, 14 };
        var weights = new[] { FontWeight.SemiBold, FontWeight.SemiBold, FontWeight.SemiBold, FontWeight.SemiBold, FontWeight.Medium, FontWeight.Normal };
        for (var level = 1; level <= 6; level++)
        {
            var heading = Assert.Single(blocks, block => block.Classes.Contains($"Heading{level}"));
            Assert.Equal(sizes[level - 1], heading.FontSize);
            Assert.Equal(weights[level - 1], heading.FontWeight);
            if (level > 1)
            {
                var previous = Assert.Single(blocks, block => block.Classes.Contains($"Heading{level - 1}"));
                Assert.Equal(24, previous.Margin.Bottom + heading.Margin.Top);
                Assert.Equal(24, heading.Bounds.Top - previous.Bounds.Bottom);
            }
            if (level == 6)
                Assert.Equal(16, heading.Margin.Bottom + body.Margin.Top);
        }
        var code = Assert.Single(view.GetLogicalDescendants().OfType<CCode>());
        var strong = Assert.Single(view.GetLogicalDescendants().OfType<CBold>());
        var link = Assert.Single(view.GetLogicalDescendants().OfType<CHyperlink>());
        Assert.Equal(FontWeight.Medium, strong.FontWeight);
        Assert.Equal(12, code.FontSize);
        Assert.Equal("JetBrains Mono", code.MonospaceFontFamily.Name);
        Assert.Equal(new Thickness(6, 2), code.Padding);
        Assert.Equal(new Thickness(2, 0), code.Margin);
        Assert.Equal(new CornerRadius(6), code.CornerRadius);
        Assert.True(link.IsUnderline);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            host.Window.RequestedThemeVariant = theme;
            host.Pump();
            AssertBrush(view, theme, "AlcolerTextPrimary", body.Foreground);
            AssertBrush(view, theme, "AlcolerTextPrimary", code.Foreground);
            AssertBrush(view, theme, "AlcolerSurface", code.Background);
            AssertBrush(view, theme, "AlcolerAccent", link.Foreground);
        }
    }

    [AvaloniaFact]
    public void Fragment_ReusesTypographyAndCodeChrome()
    {
        var fragment = new ConversationMarkdownFragment
        {
            Markdown = "# Title\n\nBody `inline`.\n\n```csharp\nvar x = 1;\n```",
        };
        using var host = new Harness(fragment);

        var heading = Assert.Single(fragment.GetVisualDescendants().OfType<CTextBlock>(),
            block => block.Classes.Contains("Heading1"));
        var body = Assert.Single(fragment.GetVisualDescendants().OfType<CTextBlock>(),
            block => block.Text.StartsWith("Body", StringComparison.Ordinal));
        var code = Assert.Single(fragment.GetVisualDescendants().OfType<CTextBlock>(),
            block => block.Classes.Contains("CodeBlock"));
        var copy = Assert.Single(fragment.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("copyCode"));

        Assert.Equal(18, heading.FontSize);
        Assert.Equal(16, heading.Margin.Bottom);
        Assert.Equal(14, body.FontSize);
        Assert.Equal("JetBrains Mono", body.FontFamily.Name);
        Assert.Equal("JetBrains Mono", code.FontFamily.Name);
        Assert.True(copy.IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(false, "`code`")]
    [InlineData(true, "`code`")]
    [InlineData(false, "Text ending in `code`")]
    [InlineData(true, "Text ending in `code`")]
    [InlineData(false, "**Nested `code`**")]
    [InlineData(true, "**Nested `code`**")]
    [InlineData(false, "- First `code`\n- Second `tail`")]
    [InlineData(true, "- First `code`\n- Second `tail`")]
    [InlineData(false, "Wrapped `C:/Projects/Alcoler/long-path-component/another-component/文件.cs`")]
    [InlineData(true, "Wrapped `C:/Projects/Alcoler/long-path-component/another-component/文件.cs`")]
    public void DecoratedInlineEnd_SelectsEveryCharacterInBothDirections(bool fragment, string markdown)
    {
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = markdown }
            : new ConversationMarkdown { Markdown = markdown };
        using var host = new Harness(view, 280);
        var blocks = view.GetVisualDescendants().OfType<CTextBlock>()
            .Where(block => block.GetLogicalDescendants().OfType<CCode>().Any()).ToArray();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            foreach (var reverse in new[] { false, true })
            {
                block.Select(reverse ? block.Text.Length : 0, reverse ? 0 : block.Text.Length);
                var selection = Assert.IsType<Selection>(block.Selection);
                Assert.Equal(0, Math.Min(selection.From, selection.To));
                Assert.Equal(block.Text.Length, Math.Max(selection.From, selection.To));
                Assert.Equal(block.Text, block.GetSelectedText());
                host.Window.Width = reverse ? 280 : 360;
                host.Pump();
                Assert.Equal(block.Text, block.GetSelectedText());
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InlineCodePartialSelection_DragAndCopyPreservePrefix(bool fragment, bool reverse)
    {
        foreach (var (markdown, prefix, clipboard) in new[]
        {
            ("`abcdef`", "", "ab"),
            ("Prefix `abcdef`", "Prefix ", "ab"),
            ("- `abcdef`", "", "\u25cb ab"),
            // Table copying retains empty cells in its TSV grid.
            ("| Key | Value |\n| --- | --- |\n| row | `abcdef` |", "", "\t\t\n\tab\t"),
        })
        {
            Control view = fragment
                ? new ConversationMarkdownFragment { Markdown = markdown }
                : new ConversationMarkdown { Markdown = markdown };
            using var host = new Harness(view);
            await host.RenderAsync();
            var blocks = view.GetVisualDescendants().OfType<CTextBlock>().ToArray();
            Assert.True(blocks.Any(text => text.Text == prefix + "abcdef"),
                $"Markdown [{markdown}] rendered [{string.Join(" | ", blocks.Select(text => text.Text))}].");
            var block = Assert.Single(blocks, text => text.Text == prefix + "abcdef");
            var code = Assert.Single(block.GetLogicalDescendants().OfType<CCode>());
            var prefixWidth = prefix.Length == 0 ? 0 : InlineTextWidth(
                Assert.Single(block.GetLogicalDescendants().OfType<CRun>(), run => run.Text == prefix), prefix);
            var start = InlineCodePoint(host, block, code, "") + new Vector(prefixWidth, 0);
            var end = InlineCodePoint(host, block, code, "ab") + new Vector(prefixWidth, 0);
            await AssertDragCopiesAsync(host, block, start, end, prefix.Length, "ab", reverse, clipboard);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InlineCodePartialSelection_PreservesUnicodeAndWhitespace(bool fragment, bool reverse)
    {
        foreach (var (text, prefix) in new[]
        {
            ("中文文件", "中文"),
            ("\U0001F600abcdef", "\U0001F600a"),
            ("e\u0301abcdef", "e\u0301a"),
            ("a bcdef", "a "),
        })
        {
            var markdown = $"`{text}`";
            Control view = fragment
                ? new ConversationMarkdownFragment { Markdown = markdown }
                : new ConversationMarkdown { Markdown = markdown };
            using var host = new Harness(view);
            await host.RenderAsync();
            var block = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(), candidate => candidate.Text == text);
            var code = Assert.Single(block.GetLogicalDescendants().OfType<CCode>());
            var start = InlineCodePoint(host, block, code, "");
            var end = InlineCodePoint(host, block, code, prefix);
            await AssertDragCopiesAsync(host, block, start, end, 0, prefix, reverse);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InlineCodePartialSelection_DragAcrossRunsAndNestedDecoratorCopiesExactText(bool fragment, bool reverse)
    {
        foreach (var nested in new[] { false, true })
        {
            Control view = fragment
                ? new ConversationMarkdownFragment { Markdown = "`abcdefgh`" }
                : new ConversationMarkdown { Markdown = "`abcdefgh`" };
            using var host = new Harness(view);
            var block = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(), text => text.Text == "abcdefgh");
            var code = Assert.Single(block.GetLogicalDescendants().OfType<CCode>());
            var first = new CRun { Text = "ab" };
            var middle = new CRun { Text = "cd" };
            var last = new CRun { Text = "efgh" };
            var inner = nested ? new CCode([middle, last]) : null;
            code.Content = inner is null ? [first, middle, last] : [first, inner];
            // Replacing CSpan.Content does not invalidate the host's measured geometries.
            var inlines = block.Content.ToArray();
            block.Content.Clear();
            foreach (var inline in inlines)
                block.Content.Add(inline);
            await host.RenderAsync();
            Assert.Equal("abcdefgh", block.Text);
            var start = InlineCodePoint(host, block, code, "a");
            var end = InlineCodePoint(host, block, code, "")
                + new Vector(InlineTextWidth(first, first.Text) + InlineTextWidth(middle, middle.Text)
                    + InlineTextWidth(last, "ef"), 0);
            if (inner is not null)
                end += new Vector(inner.Margin.Left + inner.BorderThickness.Left + inner.Padding.Left, 0);
            await AssertDragCopiesAsync(host, block, start, end, 1, "bcdef", reverse);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineCodePartialSelection_SurvivesLineReflow(bool fragment)
    {
        const string text = "abcdefgh ijklmnop qrstuvwx yz012345 abcdefgh ijklmnop qrstuvwx yz012345";
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = $"`{text}`" }
            : new ConversationMarkdown { Markdown = $"`{text}`" };
        using var host = new Harness(view);
        await host.RenderAsync();
        var block = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(), candidate => candidate.Text == text);
        var initialHeight = block.Bounds.Height;
        block.Select(1, 30);
        foreach (var width in new[] { 120d, 280d, 900d })
        {
            host.Window.Width = width;
            await host.RenderAsync();
            Assert.Equal(text[1..30], block.GetSelectedText());
            var selection = Assert.IsType<Selection>(block.Selection);
            Assert.Equal(1, selection.From);
            Assert.Equal(30, selection.To);
            if (width == 120)
                Assert.True(block.Bounds.Height > initialHeight);
        }
    }

    [AvaloniaFact]
    public async Task EmptyDecoratedInline_AllowsEmptySelection()
    {
        var code = new CCode([new CRun { Text = "" }]) { Padding = new Thickness(6, 2) };
        var block = new CTextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        block.Content.Add(new ConversationTextSpan(block, [code]));
        using var host = new Harness(block);
        await host.RenderAsync();
        Assert.Equal(0, block.GetBegin().Index);
        Assert.Equal(0, block.GetEnd().Index);
        block.Select(0, 0);
        Assert.Equal("", block.GetSelectedText());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecoratedGeometry_HitTestingPreservesRunOffsetsAndNestedLastChild(bool nested)
    {
        var last = new CRun { Text = nested ? "efgh" : "cdefgh" };
        CInline tail = nested
            ? new CCode([new CRun { Text = "cd" }, last])
            {
                Padding = new Thickness(3, 1),
                Margin = new Thickness(1, 0),
                BorderThickness = new Thickness(1),
            }
            : last;
        var first = new CRun { Text = "ab" };
        var code = new CCode([first, tail])
        {
            Padding = new Thickness(6, 2),
            Margin = new Thickness(2, 0),
            FontSize = 12,
        };
        var block = new CTextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        block.Content.Add(new ConversationTextSpan(block, [code]));
        using var host = new Harness(block);
        await host.RenderAsync();
        Assert.Equal("abcdefgh", block.Text);

        var inset = code.Margin.Left + code.BorderThickness.Left + code.Padding.Left;
        var lastOffset = nested ? 4 : 2;
        var lastX = inset + InlineTextWidth(first, first.Text);
        if (tail is CCode inner)
        {
            lastX += inner.Margin.Left + inner.BorderThickness.Left + inner.Padding.Left
                + InlineTextWidth(inner, "cd");
        }
        var y = block.Bounds.Height / 2;
        Assert.Equal(0, block.CalcuatePointerFrom(0.01, y).Index);
        Assert.Equal(1, block.CalcuatePointerFrom(inset + InlineTextWidth(first, "a") + 0.01, y).Index);
        Assert.Equal(2, block.CalcuatePointerFrom(inset + InlineTextWidth(first, first.Text), y).Index);
        foreach (var length in new[] { 0, 1, last.Text.Length - 1, last.Text.Length })
        {
            var hit = block.CalcuatePointerFrom(lastX + InlineTextWidth(last, last.Text[..length]) + 0.01, y);
            var expected = lastOffset + length;
            Assert.Equal(expected, hit.Index);
            foreach (var reverse in new[] { false, true })
            {
                block.Select(reverse ? hit.Index : 0, reverse ? 0 : hit.Index);
                Assert.Equal(block.Text[..expected], block.GetSelectedText());
                var selection = Assert.IsType<Selection>(block.Selection);
                Assert.Equal(0, Math.Min(selection.From, selection.To));
                Assert.Equal(expected, Math.Max(selection.From, selection.To));
            }
        }
        Assert.Equal(block.Text.Length, block.CalcuatePointerFrom(block.Bounds.Width - 0.01, y).Index);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineCodePointerEvents_OnlyLinkedCodeConsumesClicks(bool linked)
    {
        var clicks = 0;
        var code = new CCode([new CRun { Text = "abcdef" }])
        {
            Padding = new Thickness(6, 2),
            Margin = new Thickness(2, 0),
            FontSize = 12,
        };
        CInline inline = linked ? new CHyperlink([code]) { Command = _ => clicks++ } : code;
        var block = new CTextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        block.Content.Add(new ConversationTextSpan(block, [inline]));
        using var host = new Harness(block);
        await host.RenderAsync();
        if (linked)
            Assert.Contains(inline, code.GetLogicalAncestors());
        var presses = new List<bool>();
        var releases = new List<bool>();
        host.Window.AddHandler(InputElement.PointerPressedEvent, (_, e) => presses.Add(e.Handled),
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        host.Window.AddHandler(InputElement.PointerReleasedEvent, (_, e) => releases.Add(e.Handled),
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        var point = InlineCodePoint(host, block, code, "a");
        host.Window.MouseMove(point);
        host.Pump();
        Assert.Equal(0, clicks);
        if (inline is CHyperlink hovered)
            Assert.Contains(":hover", hovered.Classes);
        host.Window.MouseDown(point, MouseButton.Left);
        Assert.Equal(0, clicks);
        if (inline is CHyperlink pressed)
            Assert.Contains(":pressed", pressed.Classes);
        host.Window.MouseUp(point, MouseButton.Left);
        host.Pump();
        if (inline is CHyperlink released)
            Assert.DoesNotContain(":pressed", released.Classes);
        Assert.Equal(linked, Assert.Single(presses));
        Assert.Equal(linked, Assert.Single(releases));
        Assert.Equal(linked ? 1 : 0, clicks);
        var outside = new Point(host.Window.Bounds.Width - 1, host.Window.Bounds.Height - 1);
        host.Window.MouseMove(outside);
        host.Pump();
        Assert.Equal(linked ? 1 : 0, clicks);
        if (inline is CHyperlink exited)
            Assert.DoesNotContain(":hover", exited.Classes);
        if (linked)
        {
            host.Window.MouseMove(point);
            host.Window.MouseDown(point, MouseButton.Left);
            host.Window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            host.Window.MouseUp(outside, MouseButton.Left);
            host.Pump();
            Assert.Equal(1, clicks);
            host.Window.MouseMove(point);
            host.Window.MouseDown(point, MouseButton.Left);
            host.Window.MouseUp(point, MouseButton.Left);
            host.Pump();
            Assert.Equal(2, clicks);
        }
    }

    private static double InlineTextWidth(CInline inline, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(inline.FontFamily, inline.FontStyle, inline.FontWeight), inline.FontSize, Brushes.Black)
            .WidthIncludingTrailingWhitespace;

    private static Point InlineCodePoint(Harness host, CTextBlock block, CCode code, string prefix)
    {
        var x = code.Margin.Left + code.BorderThickness.Left + code.Padding.Left + InlineTextWidth(code, prefix) + 0.01;
        return block.TranslatePoint(new Point(x, block.Bounds.Height / 2), host.Window)!.Value;
    }

    private static async Task AssertDragCopiesAsync(Harness host, CTextBlock block, Point start, Point end,
        int startIndex, string expected, bool reverse, string? clipboardText = null)
    {
        try
        {
            var from = reverse ? end : start;
            var to = reverse ? start : end;
            host.Window.MouseMove(from);
            host.Window.MouseDown(from, MouseButton.Left);
            host.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            host.Window.MouseUp(to, MouseButton.Left);
            await host.RenderAsync();
            Assert.Equal(expected, block.GetSelectedText());
            var selection = Assert.IsType<Selection>(block.Selection);
            Assert.Equal(startIndex, Math.Min(selection.From, selection.To));
            Assert.Equal(startIndex + expected.Length, Math.Max(selection.From, selection.To));
            await host.Window.Clipboard!.SetTextAsync("not the selection");
            host.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            host.Window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            host.Pump();
            Assert.Equal(clipboardText ?? expected, (await host.Window.Clipboard.GetTextAsync())?.TrimEnd('\r', '\n'));
        }
        finally
        {
            ConversationMarkdownFragment.ClearActiveSelection();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DecoratedListSelection_DragAndCopyPreserveCompleteText(bool fragment, bool reverse)
    {
        const string markdown = "- First `code`\n- Second `tail`\n- After";
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = markdown }
            : new ConversationMarkdown { Markdown = markdown };
        using var host = new Harness(view);
        await host.RenderAsync();
        var blocks = view.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        var first = Assert.Single(blocks, block => block.Text == "First code");
        var last = Assert.Single(blocks, block => block.Text == "Second tail");
        var after = Assert.Single(blocks, block => block.Text == "After");
        var start = first.TranslatePoint(new Point(1, first.Bounds.Height / 2), host.Window)!.Value;
        var end = after.TranslatePoint(new Point(2, after.Bounds.Height / 2), host.Window)!.Value;
        var from = reverse ? end : start;
        var to = reverse ? start : end;
        host.Window.MouseMove(from);
        host.Window.MouseDown(from, MouseButton.Left);
        if (fragment)
            Assert.True(view.IsFocused, $"Selection did not start at {from}; hit {host.Window.InputHitTest(from)}, after {after.Bounds}, view {view.Bounds}.");
        host.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(to, MouseButton.Left);
        host.Pump();

        Assert.Equal(first.Text, first.GetSelectedText());
        Assert.Equal(last.Text, last.GetSelectedText());
        await host.Window.Clipboard!.SetTextAsync("not the selection");
        host.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        host.Window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
        host.Pump();
        var copied = await host.Window.Clipboard.GetTextAsync();
        Assert.Contains(first.Text, copied);
        Assert.Contains(last.Text, copied);
        ConversationMarkdownFragment.ClearActiveSelection();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_KeepFullHeightAndScaleWithViewport(bool fragment)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-markdown-image-{Guid.NewGuid():N}.png");
        using (var bitmap = new SkiaSharp.SKBitmap(798, 264))
        {
            bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        try
        {
            var imageMarkdown = $"![Preview]({path.Replace('\\', '/')})";
            var markdown = $"Before\n\n{imageMarkdown}\n\n[{imageMarkdown}](https://example.com)\n\nAfter";
            Control view = fragment
                ? new ConversationMarkdownFragment { Markdown = markdown }
                : new ConversationMarkdown { Markdown = markdown };
            using var host = new Harness(view);
            var images = view.GetLogicalDescendants().OfType<CImage>().ToArray();
            Assert.Equal(2, images.Length);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (images.Any(image => image.Image?.Size != new Size(798, 264)))
            {
                await Task.Delay(10, timeout.Token);
                host.Pump();
            }
            var blocks = view.GetVisualDescendants().OfType<CTextBlock>().ToArray();
            var imageBlocks = blocks.Where(block => block.GetLogicalDescendants().OfType<CImage>().Any()).ToArray();
            Assert.Equal(2, imageBlocks.Length);
            var before = Assert.Single(blocks, block => block.Text == "Before");
            var after = Assert.Single(blocks, block => block.Text == "After");
            Assert.Equal(24.5, before.LineHeight);
            Assert.Equal(24.5, after.LineHeight);
            foreach (var width in new[] { 900d, 400d, 900d })
            {
                host.Window.Width = width;
                host.Pump();
                var previousBottom = before.TranslatePoint(default, view)!.Value.Y + before.Bounds.Height;
                foreach (var block in imageBlocks)
                {
                    Assert.InRange(block.Bounds.Width, 1, width);
                    var expectedHeight = 264 * Math.Min(1, block.Bounds.Width / 798);
                    Assert.InRange(block.Bounds.Height, expectedHeight, Math.Ceiling(expectedHeight));
                    var top = block.TranslatePoint(default, view)!.Value.Y;
                    Assert.True(top >= previousBottom);
                    previousBottom = top + block.Bounds.Height;
                }
                Assert.True(after.TranslatePoint(default, view)!.Value.Y >= previousBottom);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_UseConversationWorkspaceAndReloadWhenItChanges(bool streaming)
    {
        const string relativePath = "Assets/Textures/Item/Material/CircuitBoard.png";
        var root = Directory.CreateTempSubdirectory("alcoler-markdown-workspace-").FullName;
        try
        {
            var firstRoot = Path.Combine(root, "first");
            var secondRoot = Path.Combine(root, "second");
            WriteImage(Path.Combine(firstRoot, relativePath), 128, 128);
            WriteImage(Path.Combine(secondRoot, relativePath), 64, 32);
            Assert.False(File.Exists(Path.GetFullPath(relativePath)));
            var runtime = new FakeAgentRuntime();
            using var store = new ProjectionStore(() => throw new NotSupportedException(), StrongId.NewSessionId(),
                InlineUiScheduler.Instance, TestSnapshots.State());
            using var conversation = new ConversationViewModel(runtime, store) { WorkspaceRoot = firstRoot };
            var assistant = new AssistantMessageItemViewModel("image", StrongId.NewMessageId());
            assistant.UpdateFrom(new AssistantMessageRow("image", assistant.Id,
                [new TextPart(StrongId.NewPartId(), $"![电路板贴图]({relativePath})\n\nContinuing")],
                streaming, DateTimeOffset.UtcNow));
            conversation.Items.Add(assistant);
            var timeline = new TimelineView { DataContext = conversation };
            using var host = new Harness(timeline);
            Control renderer = streaming
                ? Assert.Single(timeline.GetVisualDescendants().OfType<ConversationMarkdownFragment>())
                : Assert.Single(timeline.GetVisualDescendants().OfType<ConversationMarkdown>());

            var firstImage = await WaitForImageAsync(host, renderer, new Size(128, 128));
            conversation.WorkspaceRoot = secondRoot;
            await WaitForImageAsync(host, renderer, new Size(64, 32));
            Assert.Equal(new Size(128, 128), firstImage.Image!.Size);
            if (renderer is ConversationMarkdown markdown)
            {
                markdown.WrapCodeBlocks = true;
                await WaitForImageAsync(host, renderer, new Size(64, 32));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Images_KeepDocumentWorkspacesIsolatedAcrossContextSwitches()
    {
        const string relativePath = "Assets/Textures/Item/Material/CircuitBoard.png";
        const string markdown = $"![电路板贴图]({relativePath})";
        var root = Directory.CreateTempSubdirectory("alcoler-markdown-documents-").FullName;
        try
        {
            var firstRoot = Path.Combine(root, "first");
            var secondRoot = Path.Combine(root, "second");
            WriteImage(Path.Combine(firstRoot, relativePath), 128, 128);
            WriteImage(Path.Combine(secondRoot, relativePath), 64, 32);
            using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
            workbench.ShowNewTask(firstRoot);
            var firstDocument = workbench.OpenPlanDocument("image", "Image", markdown);
            var documents = new DocumentWorkspaceView { DataContext = workbench };
            using var host = new Harness(documents);
            var firstView = Assert.Single(documents.GetVisualDescendants().OfType<PlanDocumentView>());
            var firstImage = await WaitForImageAsync(host, firstView, new Size(128, 128));

            workbench.ShowNewTask(secondRoot);
            var secondDocument = workbench.OpenPlanDocument("image", "Image", markdown);
            host.Pump();
            var secondView = Assert.Single(documents.GetVisualDescendants().OfType<PlanDocumentView>(),
                view => ReferenceEquals(view.DataContext, secondDocument.Content));
            await WaitForImageAsync(host, secondView, new Size(64, 32));
            Assert.Equal(2, workbench.Documents.Items.Count);
            Assert.Equal(new Size(128, 128), firstImage.Image!.Size);

            workbench.ShowNewTask(firstRoot);
            host.Pump();
            Assert.Same(firstDocument, workbench.Documents.ActiveDocument);
            Assert.Same(firstView, Assert.Single(documents.GetVisualDescendants().OfType<PlanDocumentView>(),
                view => ReferenceEquals(view.DataContext, firstDocument.Content)));
            await WaitForImageAsync(host, firstView, new Size(128, 128));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Images_UseConversationWorkspaceInQuestionPreviews()
    {
        const string relativePath = "Assets/Textures/Item/Material/CircuitBoard.png";
        var root = Directory.CreateTempSubdirectory("alcoler-markdown-preview-").FullName;
        try
        {
            WriteImage(Path.Combine(root, relativePath), 128, 128);
            var state = TestSnapshots.State() with
            {
                PendingInteraction = new InteractionRequest(StrongId.NewInteractionId(), "Choose",
                    [new InteractionQuestion("Preview", "Which image?",
                        [new InteractionOption("Circuit board", Preview: $"![电路板贴图]({relativePath})")])]),
            };
            var runtime = new FakeAgentRuntime();
            using var store = new ProjectionStore(() => throw new NotSupportedException(), StrongId.NewSessionId(),
                InlineUiScheduler.Instance, state);
            using var conversation = new ConversationViewModel(runtime, store) { WorkspaceRoot = root };
            var interaction = new InteractionAreaView { DataContext = conversation };
            using var host = new Harness(interaction);
            var preview = Assert.Single(interaction.GetVisualDescendants().OfType<ConversationMarkdown>(),
                view => view.Name == "QuestionPreview");
            await WaitForImageAsync(host, preview, new Size(128, 128));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteImage(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static async Task<CImage> WaitForImageAsync(Harness host, Control view, Size expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            host.Pump();
            var image = Assert.Single(view.GetLogicalDescendants().OfType<CImage>());
            if (image.Image?.Size == expected)
                return image;
            Assert.True(DateTime.UtcNow < deadline, $"Expected image size {expected}, got {image.Image?.Size}.");
            await Task.Delay(10);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false, "image")]
    [InlineData(true, false, "image")]
    [InlineData(false, true, "image")]
    [InlineData(true, true, "image")]
    [InlineData(false, false, "text")]
    [InlineData(true, false, "text")]
    [InlineData(false, true, "text")]
    [InlineData(true, true, "text")]
    [InlineData(false, false, "binary")]
    [InlineData(true, false, "binary")]
    [InlineData(false, true, "binary")]
    [InlineData(true, true, "binary")]
    [InlineData(false, false, "oversized-image")]
    [InlineData(true, false, "oversized-image")]
    [InlineData(false, true, "oversized-image")]
    [InlineData(true, true, "oversized-image")]
    public async Task Hyperlinks_PreferInternalViewersAndPreserveOriginContext(bool fragment, bool document, string kind)
    {
        var extension = kind is "image" or "oversized-image" ? "png" : kind == "text" ? "cfg" : "kra";
        var relativePath = $"Artwork/SpriteModels/.tmp/street-trash-bin-update/game-four-facings-daylight.{extension}";
        var root = Directory.CreateTempSubdirectory("alcoler-markdown-link-").FullName;
        try
        {
            var path = Path.GetFullPath(relativePath, root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (kind == "image")
                WriteImage(path, 640, 320);
            else if (kind == "oversized-image")
            {
                using var file = File.Create(path);
                file.SetLength(ComposerImageAttachment.MaxImageBytes + 1L);
            }
            else if (kind == "text")
                File.WriteAllText(path, "configuration = true");
            else
                File.WriteAllBytes(path, [0x50, 0x4b, 3, 4, 0, 0]);
            var runtime = new FakeAgentRuntime();
            var launcher = new RecordingLinkLauncher();
            using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]),
                externalEditorLauncher: launcher);
            workbench.ShowNewTask(Path.Combine(root, "other-workspace"));
            var ownerId = document ? "source-owner" : workbench.CurrentDocumentOwnerId;
            using var store = new ProjectionStore(() => throw new NotSupportedException(), StrongId.NewSessionId(),
                InlineUiScheduler.Instance, TestSnapshots.State());
            using var conversation = new ConversationViewModel(runtime, store) { WorkspaceRoot = root };
            var markdown = $"[查看游戏内四朝向预览]({relativePath})";
            Control view = fragment
                ? new ConversationMarkdownFragment { Markdown = markdown }
                : new ConversationMarkdown { Markdown = markdown };
            view.DataContext = new object();
            var scope = new ContentControl
            {
                DataContext = document
                    ? new DocumentViewModel("plan", "plan", "Plan", "", new PlanDocumentViewModel("Plan", markdown), root, ownerId)
                    : conversation,
                Content = view,
            };
            using var host = new Harness(scope);
            host.Window.DataContext = workbench;
            await host.RenderAsync();

            if (kind == "image")
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(GlyphRun).TypeHandle);
                using var decoder = AvaloniaLocator.EnterScope();
                Avalonia.Skia.SkiaPlatform.Initialize();
                ClickLink();
                var overlay = Assert.Single(host.Window.GetVisualDescendants().OfType<ImageViewerOverlay>());
                try
                {
                    await WaitForAsync(host, () => overlay.FindControl<Image>("PreviewImage")!.Source is not null);
                    Assert.Equal(Path.GetFileName(path), overlay.FindControl<TextBlock>("FileNameLabel")!.Text);
                    Assert.Equal(new PixelSize(640, 320), Assert.IsType<Bitmap>(overlay.FindControl<Image>("PreviewImage")!.Source).PixelSize);
                    Assert.Empty(launcher.Opened);
                    Assert.Null(workbench.Banner);
                }
                finally
                {
                    overlay.Close();
                }
                return;
            }

            ClickLink();

            if (kind == "text")
            {
                await WaitForAsync(host, () => workbench.Documents.Items.Any(item =>
                    item.Kind == "file" && item.Content is TextDocumentViewModel { IsLoading: false }));
                var fileDocument = Assert.Single(workbench.Documents.Items, item => item.Kind == "file");
                var preview = Assert.IsType<TextDocumentViewModel>(fileDocument.Content);
                Assert.Equal(path, preview.FilePath);
                Assert.Equal("configuration = true", preview.Text);
                Assert.Null(preview.Error);
                Assert.Equal(root, fileDocument.WorkspaceRoot);
                Assert.Equal(ownerId, fileDocument.OwnerId);
                Assert.Empty(launcher.Opened);
                Assert.Null(workbench.Banner);
                return;
            }

            await WaitForAsync(host, () => launcher.Opened.Count > 0);
            Assert.Equal(path, Assert.Single(launcher.Opened));
            Assert.Empty(host.Window.GetVisualDescendants().OfType<ImageViewerOverlay>());
            Assert.True(File.Exists(launcher.Opened[0]));
            Assert.Null(workbench.Banner);

            if (view is ConversationMarkdown rich)
                rich.WrapCodeBlocks = true;
            launcher.Error = new Win32Exception(2);
            await host.RenderAsync();

            ClickLink();

            await WaitForAsync(host, () => workbench.Banner is not null);
            Assert.Contains(Path.GetFileName(path), workbench.Banner);
            Assert.Contains(launcher.Error.Message, workbench.Banner);

            void ClickLink()
            {
                var block = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(),
                    text => text.Text == "查看游戏内四朝向预览");
                var point = block.TranslatePoint(new Point(8, block.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseMove(point);
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                host.Pump();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForAsync(Harness host, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            host.Pump();
            Assert.True(DateTime.UtcNow < deadline, "The hyperlink action did not complete.");
            await Task.Delay(10);
        }
        host.Pump();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsQuotesTablesAndRules_KeepNeutralScopedAppearance(bool fragment)
    {
        const string markdown = "Before\n\n- first\n  - nested\n- second\n\n1. ordered\n2. next\n\n> quoted\n>\n> second quote paragraph\n\n| Name | Value |\n| --- | --- |\n| one | two |\n| three | four |\n\n---\n\nAfter";
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = markdown }
            : new ConversationMarkdown { Markdown = markdown };
        using var host = new Harness(view);
        var markers = view.GetVisualDescendants().OfType<CTextBlock>()
            .Where(block => block.Classes.Contains("ListMarker")).ToArray();
        Assert.Equal(5, markers.Length);
        Assert.All(markers.Where(marker => !char.IsDigit(marker.Text[0])), marker => Assert.Equal("•", marker.Text));
        Assert.Contains(markers, marker => marker.Text == "1.");
        Assert.Contains(markers, marker => marker.Text == "2.");
        var lists = view.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Classes.Contains("List")).ToArray();
        Assert.Equal(3, lists.Length);
        Assert.Contains(lists, list => list.Margin.Top == 6);
        Assert.All(lists, list =>
        {
            Assert.Equal(20, list.Margin.Left);
            Assert.Equal(6, list.RowSpacing);
        });
        Assert.All(view.GetVisualDescendants().OfType<CTextBlock>().Where(block => block.Text is "first" or "nested" or "second"),
            block => Assert.Equal(0, block.Margin.Bottom));
        var quote = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("Blockquote"));
        Assert.Equal(default, quote.BorderThickness);
        Assert.Equal(new Thickness(12, 0, 0, 0), quote.Padding);
        var quoteParagraphs = quote.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        Assert.Equal(2, quoteParagraphs.Length);
        Assert.Equal(0, quoteParagraphs[0].Margin.Top);
        Assert.Equal(8, quoteParagraphs[1].Margin.Top);
        var table = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("Table"));
        Assert.Equal(default, table.BorderThickness);
        Assert.Equal(new CornerRadius(12), table.CornerRadius);
        var tableScroll = Assert.IsType<ScrollViewer>(table.Child);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, tableScroll.HorizontalScrollBarVisibility);
        var tableGrid = Assert.IsType<Grid>(tableScroll.Content);
        Assert.All(tableGrid.Children.OfType<Border>(), cell => Assert.Equal(new Thickness(12, 8), cell.Padding));
        var header = Assert.Single(table.GetVisualDescendants().OfType<CTextBlock>(), block => block.Text == "Name");
        Assert.Equal(FontWeight.Normal, header.FontWeight);
        var rule = Assert.Single(view.GetVisualDescendants().OfType<Rule>());
        Assert.False(rule.IsVisible);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            host.Window.RequestedThemeVariant = theme;
            host.Pump();
            AssertBrush(view, theme, "AlcolerTextTertiary", markers[0].Foreground);
            AssertBrush(view, theme, "AlcolerTextSecondary", quoteParagraphs[0].Foreground);
            AssertBrush(view, theme, "AlcolerTextTertiary", header.Foreground);
        }
    }

    [AvaloniaFact]
    public void ShortTables_FillTheirViewportAndDistributeCellsWhenResized()
    {
        var view = new ConversationMarkdown
        {
            Markdown = "| Name | Value |\n| --- | --- |\n| one | two |\n| three | four |",
        };
        using var host = new Harness(view, 900);
        var table = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("Table"));
        var scroller = Assert.IsType<ScrollViewer>(table.Child);
        var grid = Assert.IsType<Grid>(scroller.Content);
        Assert.All(grid.ColumnDefinitions, column => Assert.True(column.Width.IsStar));
        foreach (var width in new[] { 900d, 360d, 700d })
        {
            host.Window.Width = width;
            host.Pump();
            Assert.True(scroller.Viewport.Width > 0);
            Assert.Equal(scroller.Viewport.Width, grid.MinWidth, 2);
            Assert.Equal(scroller.Viewport.Width, grid.Bounds.Width, 2);
            Assert.Equal(scroller.Viewport.Width, scroller.Extent.Width, 2);
            foreach (var row in grid.Children.OfType<Border>().GroupBy(Grid.GetRow))
            {
                var cells = row.OrderBy(Grid.GetColumn).ToArray();
                Assert.Equal(2, cells.Length);
                Assert.Equal(0, cells[0].Bounds.Left, 2);
                Assert.Equal(cells[0].Bounds.Right, cells[1].Bounds.Left, 2);
                Assert.Equal(scroller.Viewport.Width, cells[^1].Bounds.Right, 2);
                Assert.Equal(cells[0].Bounds.Width, cells[1].Bounds.Width, 2);
            }
        }
    }

    [AvaloniaFact]
    public void Fences_HandleStreamingTildesLongDelimitersAndUnknownLanguages()
    {
        var view = new ConversationMarkdown();
        using var host = new Harness(view);
        foreach (var delimiter in new[] { "```", "~~~~" })
        {
            view.Markdown = delimiter + "unknown-language\nfirst\n``` inner\n";
            host.Pump();
            Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("CodeBlock"));
            Assert.Equal("unknown-language", Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Classes.Contains("codeLanguage")).Text);
            Assert.Equal("first\n``` inner", Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(),
                block => block.Classes.Contains("CodeBlock")).Text);
            view.Markdown += delimiter + delimiter[0] + "\n\nAfter";
            host.Pump();
            Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("CodeBlock"));
            Assert.Contains(view.GetVisualDescendants().OfType<CTextBlock>(), block => block.Text == "After");
        }
        view.Markdown = "```\nplain\n```";
        host.Pump();
        Assert.Equal("text", Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Classes.Contains("codeLanguage")).Text);
    }

    [AvaloniaFact]
    public void ProseReflowsWhileLongCodeAndTablesScrollLocally()
    {
        var prose = string.Join(" ", Enumerable.Repeat("Assistant prose and 中文内容 reflow within the conversation.", 12));
        var longLine = new string('x', 300);
        var view = new ConversationMarkdown
        {
            Markdown = prose + "\n\n- Inline `C:\\Projects\\Alcoler\\src\\Alcoler\\Views\\TimelineView.axaml`\n\n```csharp\n" +
                longLine + "\n```\n\n| Wide column | Value |\n| --- | --- |\n| " + longLine + " | cell |",
        };
        using var host = new Harness(view, 900);
        var body = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(), block => block.Text == prose);
        var wideHeight = body.Bounds.Height;
        host.Window.Width = 360;
        host.Pump();
        Assert.True(body.Bounds.Height > wideHeight);
        var viewer = Assert.Single(view.GetVisualDescendants().OfType<MarkdownScrollViewer>());
        Assert.True(viewer.SelectionEnabled);
        var scrolls = viewer.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
        var codeScroll = Assert.Single(scrolls, scroll => scroll.Classes.Contains("CodeBlock"));
        var tableScroll = Assert.Single(scrolls, scroll => scroll.Classes.Contains("markdownTableScroll"));
        Assert.True(codeScroll.Extent.Width > codeScroll.Viewport.Width);
        Assert.True(tableScroll.Extent.Width > tableScroll.Viewport.Width);
        var rootScroll = scrolls[0];
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, rootScroll.HorizontalScrollBarVisibility);
        Assert.True(rootScroll.Extent.Width <= rootScroll.Viewport.Width);
        foreach (var border in view.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("CodeBlock") || border.Classes.Contains("Table")))
        {
            var origin = border.TranslatePoint(default, view)!.Value;
            Assert.True(origin.X >= 0 && origin.X + border.Bounds.Width <= view.Bounds.Width);
        }
        host.Window.Width = 900;
        host.Pump();
        Assert.True(body.Bounds.Height < wideHeight + 1);
        view.SelectionEnabled = false;
        Assert.False(viewer.SelectionEnabled);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyCode_WorksWithMouseAndKeyboardAndPreservesTrailingNewline(bool fragment)
    {
        const string code = "Console.WriteLine(\"hello\");\n\nreturn;\n";
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = "```csharp\n" + code + "```" }
            : new ConversationMarkdown { Markdown = "```csharp\n" + code + "```" };
        using var host = new Harness(view);
        var copy = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("copyCode"));
        var block = Assert.Single(view.GetVisualDescendants().OfType<CTextBlock>(), text => text.Classes.Contains("CodeBlock"));
        Assert.Equal(12, block.FontSize);
        Assert.Equal("JetBrains Mono", block.FontFamily.Name);
        Assert.Equal(TextWrapping.NoWrap, block.TextWrapping);
        foreach (var keyboard in new[] { false, true })
        {
            await host.Window.Clipboard!.SetTextAsync("not the code");
            await host.RenderAsync();
            if (keyboard)
            {
                copy.Focus();
                host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            }
            else
            {
                var point = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseMove(point);
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
            }
            host.Pump();
            Assert.Equal(code, await host.Window.Clipboard.GetTextAsync());
            Assert.Equal("Copied", copy.Content);
        }
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            host.Window.RequestedThemeVariant = theme;
            host.Pump();
            var border = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("CodeBlock"));
            Assert.Equal(new CornerRadius(12), border.CornerRadius);
            AssertBrush(view, theme, "AlcolerCard", border.Background);
            Assert.Equal(default, border.BorderThickness);
            AssertBrush(view, theme, "AlcolerTextPrimary", block.Foreground);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedStyling_DoesNotChangeOtherMarkdownViewers(bool fragment)
    {
        Control conversation = fragment
            ? new ConversationMarkdownFragment { Markdown = "Body `inline`" }
            : new ConversationMarkdown { Markdown = "Body `inline`" };
        var other = new MarkdownScrollViewer { Markdown = "Other `inline`" };
        var panel = new StackPanel();
        panel.Children.Add(conversation);
        panel.Children.Add(other);
        using var host = new Harness(panel);
        var conversationCode = Assert.Single(conversation.GetLogicalDescendants().OfType<CCode>());
        var otherCode = Assert.Single(other.GetLogicalDescendants().OfType<CCode>());
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            host.Window.RequestedThemeVariant = theme;
            host.Pump();
            Assert.Equal(new Thickness(6, 2), conversationCode.Padding);
            Assert.Equal(new CornerRadius(6), conversationCode.CornerRadius);
            Assert.Equal(12, conversationCode.FontSize);
            Assert.Equal("JetBrains Mono", conversationCode.MonospaceFontFamily.Name);
            AssertBrush(conversation, theme, "AlcolerSurface", conversationCode.Background);
            Assert.Equal(default, otherCode.Padding);
            Assert.Equal(default, otherCode.CornerRadius);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalStyles_WrapperClassChangesDoNotReformatLongAnswers(bool fragment)
    {
        var markdown = string.Join("\n\n", Enumerable.Range(0, 64).Select(index =>
            $"Paragraph {index}. **Strong** text with `inline code` and [a link](https://example.com)."));
        Control view = fragment
            ? new ConversationMarkdownFragment { Markdown = markdown }
            : new ConversationMarkdown { Markdown = markdown };
        using var host = new Harness(view);
        await host.RenderAsync();
        var blocks = view.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        Assert.Equal(64, blocks.Length);
        var first = blocks[0];
        first.Select(1, first.Text.Length - 1);
        var selected = first.GetSelectedText();
        var bounds = blocks.Select(block => block.Bounds).ToArray();
        var code = Assert.Single(first.GetLogicalDescendants().OfType<CCode>());
        var brush = code.Background;

        view.Classes.Remove("conversationMarkdown");
        host.Pump();
        view.Classes.Add("conversationMarkdown");
        await host.RenderAsync();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 12; index++)
        {
            view.Classes.Remove("conversationMarkdown");
            host.Pump();
            view.Classes.Add("conversationMarkdown");
            host.Pump();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await host.RenderAsync();

        output.WriteLine($"12 wrapper-class cycles, fragment={fragment}: {allocated:N0} bytes allocated.");
        Assert.True(allocated < 512_000, $"Wrapper-class changes allocated {allocated:N0} bytes.");
        Assert.Equal(bounds, blocks.Select(block => block.Bounds));
        Assert.Equal(selected, first.GetSelectedText());
        Assert.Equal(new Thickness(6, 2), code.Padding);
        Assert.Equal(brush, code.Background);
        Assert.Equal(14, first.FontSize);
        Assert.Equal("JetBrains Mono", first.FontFamily.Name);
    }

    private static void AssertBrush(Control control, ThemeVariant theme, string resource, IBrush? actual) =>
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(control.FindResource(theme, resource)).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);

    private sealed class RecordingLinkLauncher : IExternalEditorLauncher
    {
        public IReadOnlyList<ExternalEditor> Editors => [];
        public List<string> Opened { get; } = [];
        public Exception? Error { get; set; }

        public void OpenDefault(string path)
        {
            if (Error is not null)
                throw Error;
            Opened.Add(path);
        }

        public void Open(ExternalEditor editor, string path) => throw new NotSupportedException();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(Control content, double width = 900)
        {
            Window = new Window { Width = width, Height = 1800, Content = content, RequestedThemeVariant = ThemeVariant.Dark };
            Window.Show();
            Pump();
        }

        public Window Window { get; }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public async Task RenderAsync()
        {
            Pump();
            var visual = ElementComposition.GetElementVisual(Window)!;
            var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await rendered.WaitAsync(TimeSpan.FromSeconds(5));
            Pump();
        }

        public void Dispose() => Window.Close();
    }
}
