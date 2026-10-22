using System;
using System.Linq;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Workbench.Controls;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Views;

public class TimelineSmokeTests
{
    private Window? _window;

    [AvaloniaFact]
    public void TimelineView_RendersEachRowKind()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 1000);
        try
        {
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new UserMessageRow("u1", "hello timeline", DateTimeOffset.UtcNow)),
                new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [
                    new ReasoningPart(StrongId.NewPartId(), "pondering the request"),
                    new TextPart(StrongId.NewPartId(), "# Title\n\nassistant body"),
                ], IsStreaming: true, At: DateTimeOffset.UtcNow)),
                new RowAppended(new ToolCallRow("t1", StrongId.NewToolCallId(), "Bash",
                    JsonDocument.Parse("{\"command\":\"ls\"}").RootElement, ToolCallStatus.Running,
                    Output: null, IsError: false, Duration: null, StartedAt: DateTimeOffset.UtcNow)),
                new RowAppended(new SystemNoticeRow("n1", "compact", "context compacted")),
            ], Reset: false));
            Pump();

            var descendants = view.GetVisualDescendants().ToList();
            Assert.Contains(descendants, d => d is SelectableTextBlock { Text: "hello timeline" });

            var markdown = Assert.Single(descendants.OfType<Markdown.Avalonia.MarkdownScrollViewer>());
            Assert.Equal("# Title\n\nassistant body", markdown.Markdown);

            Assert.Single(descendants.OfType<Expander>());
            // A collapsed Expander only materializes its header; the content stays in the logical tree.
            Assert.Contains(view.GetLogicalDescendants(),
                d => d is SelectableTextBlock { Text: "pondering the request" });

            Assert.Contains(descendants, d => d is TextBlock { Text: "Running" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "context compacted" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "Context" });

            var caret = Assert.Single(descendants.OfType<Rectangle>(),
                r => r.Classes.Contains("streamingCaret"));
            Assert.True(caret.IsVisible);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ConversationLayout_CentersResponsiveContentAndConstrainsRightAlignedUserBubble()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 1600, 800);
        try
        {
            var text = string.Join(" ", Enumerable.Repeat("A user request stays inside its conversation bubble.", 3));
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new UserMessageRow("u1", text, DateTimeOffset.UtcNow)),
                new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                    [new TextPart(StrongId.NewPartId(), "Assistant response")], false, DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            var layout = Assert.Single(view.GetVisualDescendants().OfType<ConversationLayout>());
            var items = view.FindControl<ItemsControl>("TimelineItems")!;
            foreach (var width in new[] { 360d, 447, 448, 623, 624, 863, 864, 1279, 1280, 1600, 1900, 360 })
            {
                window.Width = width;
                Pump();
                var bubble = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                    border => border.Classes.Contains("userRow"));
                var user = Assert.Single(bubble.GetVisualDescendants().OfType<SelectableTextBlock>());
                var assistant = Assert.Single(view.GetVisualDescendants().OfType<StackPanel>(),
                    panel => panel.Classes.Contains("assistantRow"));
                Assert.Equal(HorizontalAlignment.Right, bubble.HorizontalAlignment);
                Assert.Equal(new CornerRadius(12, 2, 12, 12), bubble.CornerRadius);
                Assert.Equal(TextWrapping.Wrap, user.TextWrapping);
                Assert.Equal(14, user.FontSize);
                Assert.Equal("JetBrains Mono", user.FontFamily.Name);
                Assert.Equal(24.5, user.LineHeight);
                Assert.Equal(text, user.Text);
                var paneWidth = layout.Bounds.Width;
                var sectionWidth = paneWidth >= 1280 ? Math.Min(paneWidth - 384, 1152)
                    : paneWidth >= 864 ? Math.Min(paneWidth - 96, 896)
                    : paneWidth;
                var padding = paneWidth >= 448 ? 24 : 16;
                var origin = items.TranslatePoint(default, layout)!.Value;
                Assert.InRange(items.Bounds.Width, sectionWidth - padding * 2 - 1, sectionWidth - padding * 2 + 1);
                var expectedLeft = (paneWidth - sectionWidth) / 2 + padding;
                Assert.InRange(origin.X, expectedLeft - 1, expectedLeft + 1);
                Assert.InRange(origin.Y, 19, 21);
                Assert.Equal(20, layout.Padding.Bottom);
                if (paneWidth >= 624)
                {
                    Assert.Equal(576, bubble.MaxWidth);
                }
                else
                {
                    Assert.True(double.IsPositiveInfinity(bubble.MaxWidth));
                }
                Assert.True(bubble.Bounds.Width <= Math.Min(items.Bounds.Width, paneWidth >= 624 ? 576 : items.Bounds.Width) + 1);
                var bubbleOrigin = bubble.TranslatePoint(default, items)!.Value;
                Assert.InRange(bubbleOrigin.X + bubble.Bounds.Width, items.Bounds.Width - 1, items.Bounds.Width + 1);
                AssertWithin(bubble, items);
                AssertWithin(assistant, items);
                Assert.True(view.Scroller.Extent.Width <= view.Scroller.Viewport.Width);
                Assert.Equal(0, view.Scroller.Offset.X);
            }
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ConversationLayout_InheritedStatusReservationShiftsWideContentViaTransform()
    {
        var child = new Border { Height = 32 };
        var layout = new ConversationLayout { Child = child, Padding = new Thickness(16, 0) };
        layout.Transitions = null;
        var parent = new Grid { Children = { layout } };
        var window = new Window { Content = parent, Width = 1400, Height = 200 };
        window.Show();
        try
        {
            Pump();
            var transform = Assert.IsType<TranslateTransform>(layout.RenderTransform);
            Assert.Equal(0, transform.X);
            Assert.Equal(0, layout.TargetStatusPanelShift);
            var centered = child.Bounds.X;
            ConversationLayout.SetReserveStatusPanelSpace(parent, true);
            Pump();
            Assert.True(ConversationLayout.GetReserveStatusPanelSpace(layout));
            Assert.Equal(-168, layout.TargetStatusPanelShift);
            Assert.Equal(-168, transform.X);
            Assert.Equal(centered, child.Bounds.X, 1);
            ConversationLayout.SetReserveStatusPanelSpace(parent, false);
            Pump();
            Assert.Equal(0, layout.TargetStatusPanelShift);
            Assert.Equal(0, transform.X);
            window.Width = 900;
            Pump();
            var narrow = child.Bounds.X;
            Assert.Equal((layout.Bounds.Width - ConversationLayout.CalculateSectionWidth(layout.Bounds.Width)) / 2 + 16, narrow, 1);
            window.Width = 1280;
            layout.Margin = new Thickness(0, 0, 14, 0);
            Pump();
            var withScrollbar = child.Bounds.X;
            ConversationLayout.SetReserveStatusPanelSpace(parent, true);
            Pump();
            Assert.True(layout.Bounds.Width < 1280);
            Assert.Equal(-168, layout.TargetStatusPanelShift);
            Assert.Equal(withScrollbar, child.Bounds.X, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ThinkingBlock_ToggleExpandsAndCollapsesReasoning()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 420, 400);
        try
        {
            var content = string.Join("\n", Enumerable.Repeat("Reasoning content stays readable inside its disclosure.", 30));
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                    [new ReasoningPart(StrongId.NewPartId(), content)],
                    IsStreaming: false, At: DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            var expander = Assert.Single(view.GetVisualDescendants().OfType<Expander>());
            var toggle = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>());
            var reasoning = Assert.Single(view.GetLogicalDescendants().OfType<SelectableTextBlock>(),
                block => block.Text == content);
            var markdown = Assert.Single(view.GetLogicalDescendants().OfType<ConversationMarkdown>());
            Assert.False(markdown.IsVisible);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>(),
                viewer => viewer.IsEffectivelyVisible);
            Assert.False(expander.IsExpanded);
            Assert.DoesNotContain(reasoning, view.GetVisualDescendants());

            var collapsedChevron = Assert.Single(toggle.GetVisualDescendants().OfType<Icon>(),
                icon => icon.Classes.Contains("thinkingCollapsed"));
            var expandedChevron = Assert.Single(toggle.GetVisualDescendants().OfType<Icon>(),
                icon => icon.Classes.Contains("thinkingExpanded"));
            Assert.True(collapsedChevron.IsEffectivelyVisible);
            Assert.False(expandedChevron.IsEffectivelyVisible);
            Assert.NotSame(collapsedChevron.Data, expandedChevron.Data);
            var thinking = Assert.Single(toggle.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Thinking");
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                Pump();
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextTertiary")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(thinking.Foreground).Color);
            }
            await ClickAsync(window, toggle);

            Assert.True(expander.IsExpanded);
            Assert.True(reasoning.IsEffectivelyVisible);
            Assert.True(reasoning.Bounds.Height > 0);
            Assert.False(collapsedChevron.IsEffectivelyVisible);
            Assert.True(expandedChevron.IsEffectivelyVisible);
            AssertWithin(reasoning, expander);
            AssertWithin(toggle, expander);
            var reasoningScroll = Assert.Single(expander.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.Equal(240, reasoningScroll.MaxHeight);
            Assert.True(reasoningScroll.Bounds.Height <= 240);
            Assert.True(reasoningScroll.Extent.Height > reasoningScroll.Viewport.Height);
            foreach (var icon in toggle.GetVisualDescendants().OfType<Icon>().Where(icon => icon.IsEffectivelyVisible))
            {
                AssertWithin(icon, toggle);
            }

            await ClickAsync(window, toggle);

            Assert.False(expander.IsExpanded);
            Assert.False(reasoning.IsEffectivelyVisible);
            Assert.True(collapsedChevron.IsEffectivelyVisible);
            Assert.False(expandedChevron.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Disclosures_RetainManualStateAcrossStreamingResetsAndVirtualization()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 800);
        try
        {
            var reasoning = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new ReasoningPart(StrongId.NewPartId(), "Inspect the project before the next operation.")],
                false, DateTimeOffset.UtcNow);
            var tool = new ToolCallRow("t1", StrongId.NewToolCallId(), "Bash",
                JsonSerializer.SerializeToElement(new { command = "ls" }), ToolCallStatus.Succeeded,
                [new ToolResultText("file.txt")], false, TimeSpan.FromMilliseconds(420), DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
                [new RowAppended(reasoning), new RowAppended(tool)], Reset: false));
            Pump();
            var reasoningItem = Assert.IsType<AssistantMessageItemViewModel>(viewModel.Items[0]);
            var toolItem = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[1]);
            var expander = Assert.Single(view.GetVisualDescendants().OfType<Expander>());
            await ClickAsync(window, expander.GetVisualDescendants().OfType<ToggleButton>().Single());
            await ClickAsync(window, ToolHeader(view, toolItem));
            Assert.True(reasoningItem.IsReasoningExpanded);
            Assert.True(toolItem.IsExpanded);

            var updated = reasoning with
            {
                Parts = [.. reasoning.Parts, new TextPart(StrongId.NewPartId(), "Streaming commentary")],
                IsStreaming = true,
            };
            var filler = Enumerable.Range(0, 40).Select(index => (ConversationRow)new ToolCallRow($"f{index}",
                StrongId.NewToolCallId(), "Read", JsonSerializer.SerializeToElement(new { file_path = $"file{index}.txt" }),
                ToolCallStatus.Succeeded, null, false, TimeSpan.Zero, DateTimeOffset.UtcNow)).ToArray();
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming), [], Reset: true,
                ResetRows: [updated, tool, .. filler]));
            Pump();
            Assert.Same(reasoningItem, viewModel.Items[0]);
            Assert.Same(toolItem, viewModel.Items[1]);
            Assert.True(reasoningItem.IsReasoningExpanded);
            Assert.True(toolItem.IsExpanded);
            await RenderAsync(window);
            view.Scroller.ScrollToEnd();
            await RenderAsync(window);
            var items = view.FindControl<ItemsControl>("TimelineItems")!;
            items.ScrollIntoView(0);
            view.Scroller.ScrollToHome();
            await RenderAsync(window);

            var restored = Assert.Single(view.GetVisualDescendants().OfType<Expander>(),
                control => ReferenceEquals(control.DataContext, reasoningItem));
            Assert.True(restored.IsExpanded);
            Assert.Contains(restored.GetVisualDescendants().OfType<SelectableTextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == reasoningItem.ReasoningBlocks.Single());
            Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "file.txt");
            AssertHeaderDuration(ToolHeader(view, toolItem), "420ms");

            await ClickAsync(window, restored.GetVisualDescendants().OfType<ToggleButton>().Single());
            await ClickAsync(window, ToolHeader(view, toolItem));
            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(), [], Reset: true,
                ResetRows: [updated with { IsStreaming = false }, tool, .. filler]));
            Pump();
            Assert.False(reasoningItem.IsReasoningExpanded);
            Assert.False(toolItem.IsExpanded);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void AssistantMarkdown_ReflowsWhenTimelineNarrowsAndWidens()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 1000);
        try
        {
            var paragraph = string.Join(" ", Enumerable.Repeat("Normal assistant text should reflow inside the timeline.", 8));
            var markdownText = paragraph + "\n\n- Workspace: `C:\\Projects\\Alcoler\\src\\Alcoler.Workbench\\Views\\TimelineView.axaml`" +
                "\n- Open [the project documentation](https://example.com/docs) for more details.";
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                    [new TextPart(StrongId.NewPartId(), markdownText)], IsStreaming: false, At: DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            var markdown = Assert.Single(view.GetVisualDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>());
            var body = Assert.Single(markdown.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>(),
                block => block.Text == paragraph);
            Assert.Equal(14, body.FontSize);
            Assert.Equal(24.5, body.LineHeight);
            var code = Assert.Single(markdown.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CCode>());
            var link = Assert.Single(markdown.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CHyperlink>());
            Assert.True(link.IsUnderline);
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                Pump();
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextPrimary")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(body.Foreground).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(body.Foreground).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(code.Foreground).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerSurface")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(code.Background).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerAccent")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(link.Foreground).Color);
            }
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            var wideHeight = body.Bounds.Height;

            window.Width = 360;
            Pump();

            Assert.True(markdown.SelectionEnabled);
            Assert.Equal(markdownText, markdown.Markdown);
            Assert.True(body.Bounds.Height > wideHeight, $"Body height did not grow: wide {wideHeight}, narrow {body.Bounds.Height}");
            foreach (var block in markdown.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>())
            {
                var origin = block.TranslatePoint(default, view)!.Value;
                Assert.True(origin.X >= 0 && origin.X + block.Bounds.Width <= view.Bounds.Width,
                    $"Markdown block escapes timeline: x {origin.X}, width {block.Bounds.Width}, timeline {view.Bounds.Width}");
            }
            Assert.True(view.Scroller.Extent.Width <= view.Scroller.Viewport.Width);
            Assert.Equal(0, view.Scroller.Offset.X);

            var narrowHeight = body.Bounds.Height;
            window.Width = 900;
            Pump();
            Assert.True(body.Bounds.Height < narrowHeight);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ToolCallRow_RendersFileChangeCountsAndHidesZeroSides()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 1000);
        try
        {
            var row = new ToolCallRow("t1", StrongId.NewToolCallId(), "Edit",
                JsonSerializer.SerializeToElement(new { file_path = "src/AgentRuntime.cs" }),
                ToolCallStatus.Succeeded, [new ToolResultText("updated")], IsError: false,
                Duration: TimeSpan.FromMilliseconds(20), StartedAt: DateTimeOffset.UtcNow,
                FileChange: new FileChangeStats(2, 5));
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
                [new RowAppended(row)], Reset: false));
            Pump();

            var toolItem = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            var button = ToolHeader(view, toolItem);
            var added = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "+2");
            var removed = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "-5");
            AssertWithin(added, button);
            AssertWithin(removed, button);

            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
                [new RowUpserted(row with { FileChange = new FileChangeStats(0, 3) })], Reset: false));
            Pump();
            Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "+0");
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "-3");

            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(),
                [new RowUpserted(row with { FileChange = new FileChangeStats(2, 0) })], Reset: false));
            Pump();
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "+2");
            Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "-0");
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(ToolCallStatus.Succeeded, 1500, "1.5s")]
    [InlineData(ToolCallStatus.Failed, 1500, "1.5s")]
    [InlineData(ToolCallStatus.Failed, 420, "420ms")]
    public async Task ToolCallRow_CompletedDurationRemainsInCollapsedHeaderAcrossUpsertsAndDisclosure(
        ToolCallStatus status, int milliseconds, string duration)
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 1000);
        try
        {
            var input = JsonSerializer.SerializeToElement(new { command = "ls", description = new string('x', 240) });
            var running = new ToolCallRow("t1", StrongId.NewToolCallId(), "Bash", input,
                ToolCallStatus.Running, Output: null, IsError: false, Duration: null,
                StartedAt: DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
                [new RowAppended(running)], Reset: false));
            Pump();

            var toolItem = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            var button = ToolHeader(view, toolItem);
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == toolItem.DisplayStatusText);
            Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == duration);

            var completed = running with
            {
                Status = status,
                Output = [new ToolResultText("total 0\nfile.txt")],
                IsError = status == ToolCallStatus.Failed,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
            };
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
                [new RowUpserted(completed)], Reset: false));
            Pump();

            Assert.Same(toolItem, Assert.Single(viewModel.Items));
            Assert.False(toolItem.IsExpanded);
            AssertHeaderDuration(button, duration);
            Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text is "Running" or "Succeeded" or "running" or "succeeded");
            if (status == ToolCallStatus.Failed)
            {
                Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.IsEffectivelyVisible && block.Text == toolItem.DisplayStatusText);
            }
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == toolItem.InputJson);

            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                foreach (var width in new[] { 900d, 360 })
                {
                    window.Width = width;
                    Pump();
                    AssertHeaderDuration(button, duration);
                    AssertWithin(button, view);
                    Assert.True(button.Bounds.Height <= 48, $"Tool header is not compact: {button.Bounds.Height}");
                    foreach (var control in button.GetVisualDescendants().OfType<Control>()
                                 .Where(control => control.IsEffectivelyVisible && control is TextBlock or Icon))
                    {
                        AssertWithin(control, button);
                    }
                    Assert.True(view.Scroller.Extent.Width <= view.Scroller.Viewport.Width);
                    Assert.Equal(0, view.Scroller.Offset.X);
                }
            }

            var updated = completed with { Output = [new ToolResultText("updated tool output")] };
            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(),
                [new RowUpserted(updated)], Reset: false));
            Pump();
            Assert.Same(toolItem, Assert.Single(viewModel.Items));
            Assert.False(toolItem.IsExpanded);
            AssertHeaderDuration(ToolHeader(view, toolItem), duration);

            await ClickAsync(window, button);
            Assert.True(toolItem.IsExpanded);
            AssertHeaderDuration(button, duration);
            Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == toolItem.InputJson);
            var output = Assert.Single(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "updated tool output");
            Assert.Equal(14, output.FontSize);
            Assert.Equal(20, output.LineHeight);
            Assert.Equal("JetBrains Mono", output.FontFamily.Name);

            viewModel.ApplyUpdate(new ProjectionUpdate(4, TestSnapshots.State(),
                [new RowUpserted(updated with { Output = [new ToolResultText("final tool output")] })], Reset: false));
            Pump();
            Assert.True(toolItem.IsExpanded);
            AssertHeaderDuration(button, duration);
            Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "final tool output");

            await ClickAsync(window, button);
            Assert.False(toolItem.IsExpanded);
            AssertHeaderDuration(button, duration);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "final tool output");
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(1200)]
    public async Task WorkRows_KeepCompactSpacingAcrossToolsAndReasoning(double width)
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, width, 700);
        try
        {
            var input = JsonSerializer.SerializeToElement(new { file_path = "AGENTS.md" });
            ToolCallRow Tool(string rowId) => new(rowId, StrongId.NewToolCallId(), "Read", input,
                ToolCallStatus.Succeeded, Output: [new ToolResultText("File contents")], IsError: false,
                Duration: TimeSpan.FromMilliseconds(3), StartedAt: DateTimeOffset.UtcNow);
            var reasoning = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new ReasoningPart(StrongId.NewPartId(), "Review the workspace before reading the next file.")],
                IsStreaming: false, At: DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(Tool("t1")),
                new RowAppended(Tool("t2")),
                new RowAppended(reasoning),
                new RowAppended(Tool("t3")),
                new RowAppended(new AssistantMessageRow("a2", StrongId.NewMessageId(),
                    [new TextPart(StrongId.NewPartId(), "The workspace is ready.")], false, DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            var tools = viewModel.Items.OfType<ToolCallItemViewModel>().ToArray();
            var headers = tools.Select(item => ToolHeader(view, item)).ToArray();
            var expander = Assert.Single(view.GetVisualDescendants().OfType<Expander>(),
                control => control.IsEffectivelyVisible);
            var toggle = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>());
            var assistant = Assert.Single(view.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Classes.Contains("assistantRow") &&
                         panel.DataContext is AssistantMessageItemViewModel { RowId: "a1" });
            var answer = Assert.Single(view.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Classes.Contains("assistantRow") &&
                         panel.DataContext is AssistantMessageItemViewModel { RowId: "a2" });
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                Pump();
                Control[] workRows = [headers[0], headers[1], toggle, headers[2]];
                for (var index = 0; index < workRows.Length; index++)
                {
                    Assert.InRange(workRows[index].Bounds.Height, 27, 29);
                    if (index > 0)
                    {
                        var previous = workRows[index - 1].TranslatePoint(default, view)!.Value;
                        var current = workRows[index].TranslatePoint(default, view)!.Value;
                        Assert.InRange(current.Y - previous.Y, 31, 33);
                    }
                }
                Assert.Equal(4, assistant.Margin.Bottom);
                Assert.Equal(20, answer.Margin.Bottom);
                foreach (var header in headers)
                    AssertHeaderDuration(header, "3ms");
            }

            await ClickAsync(window, toggle);
            Assert.True(expander.IsExpanded);
            var expandedOrigin = expander.TranslatePoint(default, view)!.Value;
            var nextOrigin = headers[2].TranslatePoint(default, view)!.Value;
            Assert.InRange(nextOrigin.Y - expandedOrigin.Y - expander.Bounds.Height, 3, 5);
            await ClickAsync(window, toggle);
            Assert.False(expander.IsExpanded);
            Assert.InRange(headers[2].TranslatePoint(default, view)!.Value.Y -
                           toggle.TranslatePoint(default, view)!.Value.Y, 31, 33);

            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
            [
                new RowUpserted(reasoning with
                {
                    Parts = [.. reasoning.Parts, new TextPart(StrongId.NewPartId(), "I will read the next file.")],
                }),
            ], Reset: false));
            Pump();
            Assert.Equal(20, assistant.Margin.Bottom);
            Assert.Contains("hasText", assistant.Classes);
            AssertHeaderDuration(ToolHeader(view, tools[2]), "3ms");
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ToolCallHeaders_UseFriendlyLabelsAndTypeSpecificIcons()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 800);
        try
        {
            var tools = new (string Name, string Label)[]
            {
                ("Bash", "Terminal"), ("Read", "Read"), ("Edit", "Edit"), ("Write", "Write"),
                ("Glob", "Search"), ("Grep", "Search"), ("Browser", "Browser"),
                ("TodoRead", "Tasks"), ("TodoWrite", "Tasks"), ("ExitPlanMode", "Plan"),
            };
            var input = JsonSerializer.SerializeToElement(new { description = "Inspect the workspace" });
            var rows = tools.Select((tool, index) => new RowAppended(new ToolCallRow($"t{index}",
                StrongId.NewToolCallId(), tool.Name, input, ToolCallStatus.Succeeded,
                Output: null, IsError: false, Duration: TimeSpan.FromMilliseconds(1500),
                StartedAt: DateTimeOffset.UtcNow))).ToArray();
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), rows, Reset: false));
            Pump();

            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                Pump();
                foreach (var item in viewModel.Items.OfType<ToolCallItemViewModel>())
                {
                    var header = ToolHeader(view, item);
                    var texts = header.GetVisualDescendants().OfType<TextBlock>().ToArray();
                    var expectedLabel = tools.Single(tool => tool.Name == item.ToolName).Label;
                    Assert.Equal(expectedLabel, item.DisplayName);
                    var label = Assert.Single(texts, block => block.IsEffectivelyVisible && block.Text == expectedLabel);
                    Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
                    Assert.DoesNotContain('_', item.DisplayName);
                    Assert.Equal(14, label.FontSize);
                    Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextTertiary")).Color,
                        Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
                    var summary = Assert.Single(texts, block => block.IsEffectivelyVisible && block.Text == "Inspect the workspace");
                    Assert.Equal(14, summary.FontSize);
                    Assert.Equal(TextTrimming.CharacterEllipsis, summary.TextTrimming);
                    Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextTertiary")).Color,
                        Assert.IsAssignableFrom<ISolidColorBrush>(summary.Foreground).Color);
                    Assert.DoesNotContain(texts, block => block.IsEffectivelyVisible && block.Text is "Succeeded" or "succeeded");
                    AssertHeaderDuration(header, "1.5s");
                    if (item.ToolName is "TodoRead" or "TodoWrite" or "ExitPlanMode")
                    {
                        Assert.NotEqual(item.ToolName, item.DisplayName);
                        Assert.DoesNotContain(texts, block => block.IsEffectivelyVisible && block.Text == item.ToolName);
                    }

                    var icon = header.GetVisualDescendants().OfType<Icon>().First();
                    Assert.True(icon.IsEffectivelyVisible);
                    Assert.False(string.IsNullOrWhiteSpace(item.IconPath));
                    Assert.NotNull(icon.Data);
                    Assert.Equal(Geometry.Parse(item.IconPath).ToString(), icon.Data.ToString());
                    Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextTertiary")).Color,
                        Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);
                    AssertWithin(icon, header);
                }
            }

            var distinctTypes = viewModel.Items.OfType<ToolCallItemViewModel>()
                .Where(item => item.ToolName is not ("Glob" or "TodoWrite"))
                .Select(item => item.IconPath).ToArray();
            Assert.Equal(8, distinctTypes.Distinct().Count());
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentDispatch_UsesSingleLineSummaryWithoutInlineResultOrDisclosure(bool failed)
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var id = StrongId.NewToolCallId();
            var title = string.Join(" ", Enumerable.Repeat("Review the workspace", 12));
            var tool = new ToolCallRow("dispatch", id, "Agent",
                JsonSerializer.SerializeToElement(new { description = title, subagent_type = "Explore" }),
                ToolCallStatus.Succeeded, [new ToolResultText("# Raw child result")], false, TimeSpan.FromSeconds(60), DateTimeOffset.UtcNow);
            var state = new Alcoler.Contracts.Subagents.SubagentState(StrongId.NewSubagentId(), StrongId.NewSessionId(), title,
                failed ? Alcoler.Contracts.Subagents.SubagentStatus.Failed : Alcoler.Contracts.Subagents.SubagentStatus.Completed,
                "# Raw child result", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, ParentToolCallId: id, AgentType: "Explore");
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], true,
                [tool, new SystemNoticeRow("agent", "subagent", "Completed - # Raw child result", state)]));
            Pump();
            var item = Assert.IsType<AgentCallItemViewModel>(Assert.Single(viewModel.Items));
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 900d, 360 })
            {
                window.RequestedThemeVariant = variant;
                window.Width = width;
                Pump();
                var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("agentRow"));
                var texts = header.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).ToArray();
                Assert.Contains(texts, block => block.Text == "SubAgent");
                var type = Assert.Single(texts, block => block.Text == "Explore");
                Assert.Equal("JetBrains Mono", type.FontFamily.Name);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerAgentExplore")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(type.Foreground).Color);
                Assert.Equal(failed, texts.Any(block => block.Text == "Failed"));
                Assert.DoesNotContain(texts, block => block.Text is "Completed" or "Running" or "60.0s" || block.Text!.Contains("Raw child result"));
                Assert.DoesNotContain(header.GetVisualDescendants().OfType<Icon>(), icon => icon.Classes.Contains("disclosure"));
                Assert.Empty(view.GetVisualDescendants().OfType<SelectableTextBlock>());
                Assert.InRange(header.Bounds.Height, 27, 29);
                AssertWithin(header, view);
                foreach (var control in header.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control is TextBlock or Icon))
                    AssertWithin(control, header);
                Assert.True(view.Scroller.Extent.Width <= view.Scroller.Viewport.Width);
                Assert.Equal(item.AccessibleText, Avalonia.Automation.AutomationProperties.GetName(header));
            }
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void AutoScroll_PinsToBottomOnlyWhenUserIsNearBottom()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 420, 260);
        try
        {
            var filler = Enumerable.Range(0, 30).Select(i => new RowAppended(
                new UserMessageRow($"u{i}", $"line {i} " + new string('x', 120), DateTimeOffset.UtcNow))).ToArray();
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), filler, Reset: false));
            Pump();

            Assert.Equal(1, view.AutoScrollCount);
            var scroller = view.Scroller;
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
                $"extent {scroller.Extent.Height} must exceed viewport {scroller.Viewport.Height}");
            Assert.True(scroller.Offset.Y > 0);

            scroller.Offset = new Vector(0, 0);
            Pump();

            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
            [
                new RowAppended(new UserMessageRow("u30", "line 30 " + new string('x', 120), DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            Assert.True(view.AutoScrollCount == 1, DescribeTimeline(view));
            Assert.Equal(0, scroller.Offset.Y);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AutoScroll_StreamingUpsertDoesNotMoveReaderAwayFromEarlierMessages()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 420, 260);
        try
        {
            var initial = string.Join("\n\n", Enumerable.Range(0, 15).Select(index =>
                $"Earlier response paragraph {index}. The reader can review this content while the assistant continues streaming."));
            var assistant = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), initial)], true, DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new UserMessageRow("u1", "Explain how the conversation renderer handles streaming.", DateTimeOffset.UtcNow)),
                new RowAppended(assistant),
            ], Reset: false));
            Pump();
            var scroller = view.Scroller;
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
            Assert.True(scroller.Offset.Y > 0);

            var items = view.FindControl<ItemsControl>("TimelineItems")!;
            items.ScrollIntoView(viewModel.Items.Count - 1);
            await RenderAsync(window);
            var viewers = view.GetVisualDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>().ToArray();
            Assert.True(viewers.Length == 1, DescribeTimeline(view));
            var markdown = viewers[0];
            var earlier = Assert.Single(markdown.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>(),
                block => block.Text.StartsWith("Earlier response paragraph 5.", StringComparison.Ordinal));
            var paragraphPosition = earlier.TranslatePoint(default, scroller);
            Assert.True(paragraphPosition.HasValue, DescribeTimeline(view));
            var target = scroller.Offset.Y + paragraphPosition.Value.Y - 24;
            Assert.True(target > 0, DescribeTimeline(view));
            scroller.Offset = new Vector(0, target);
            await RenderAsync(window);
            var readingOffset = scroller.Offset;
            var previousExtent = scroller.Extent.Height;
            var scrollCount = view.AutoScrollCount;
            Assert.True(previousExtent - scroller.Viewport.Height - readingOffset.Y > 80, DescribeTimeline(view));
            var earlierText = earlier.Text;
            var anchor = Assert.Single(view.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>(),
                block => block.Text == earlierText);
            var earlierOrigin = anchor.TranslatePoint(default, scroller)!.Value;
            Assert.True(earlierOrigin.Y >= 0 && earlierOrigin.Y < scroller.Viewport.Height,
                $"Reading paragraph is not in the viewport: y {earlierOrigin.Y}; {DescribeTimeline(view)}");

            var expanded = initial + "\n\n" + string.Join("\n\n", Enumerable.Repeat(
                "The assistant keeps streaming while the reader examines earlier content.", 30));
            var updated = assistant with { Parts = [new TextPart(StrongId.NewPartId(), expanded)] };
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
                [new RowUpserted(updated)], Reset: false));
            var retained = await WaitForParagraphAsync(window, view, earlierText);

            Assert.True(scroller.Extent.Height > previousExtent,
                $"Realized streaming answer did not grow: before {previousExtent}, after {scroller.Extent.Height}");
            Assert.Equal(readingOffset, scroller.Offset);
            var currentMarkdown = Assert.Single(view.GetVisualDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>());
            Assert.Equal(expanded, currentMarkdown.Markdown);
            Assert.Equal(earlierOrigin, retained.TranslatePoint(default, scroller)!.Value);
            Assert.Equal(scrollCount, view.AutoScrollCount);

            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(),
                [new RowUpserted(updated with { IsStreaming = false })], Reset: false));
            var completed = await WaitForParagraphAsync(window, view, earlierText);
            Assert.Equal(readingOffset, scroller.Offset);
            Assert.Equal(earlierOrigin, completed.TranslatePoint(default, scroller)!.Value);
            Assert.Equal(scrollCount, view.AutoScrollCount);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ScrollExtent_MixedToolHistoryRetainsMeasuredAnswerHeightWhileReadingEarlierRows()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var tools = Enumerable.Range(0, 100).Select(index => (ConversationRow)new ToolCallRow($"t{index}",
                StrongId.NewToolCallId(), "Read", JsonSerializer.SerializeToElement(new { file_path = $"file{index}.txt" }),
                ToolCallStatus.Succeeded, null, false, TimeSpan.FromMilliseconds(3), DateTimeOffset.UtcNow)).ToArray();
            var text = string.Join("\n\n", Enumerable.Range(0, 60).Select(index =>
                $"Long answer paragraph {index}. The measured height of this streaming message must remain known while reading earlier operations."));
            var assistant = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), text)], true, DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(TurnPhase.Streaming), [], Reset: true,
                ResetRows: [.. tools, assistant]));
            var items = view.FindControl<ItemsControl>("TimelineItems")!;
            items.ScrollIntoView(viewModel.Items.Count - 1);
            await RenderAsync(window);
            view.Scroller.ScrollToEnd();
            await RenderAsync(window);
            var measuredExtent = view.Scroller.Extent.Height;
            Assert.True(measuredExtent > 4000);
            view.Scroller.ScrollToHome();
            await RenderAsync(window);
            Assert.InRange(view.Scroller.Extent.Height, measuredExtent - 64, measuredExtent + 64);
            var offset = view.Scroller.Offset;
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(assistant with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text)] })], Reset: false));
            await RenderAsync(window);
            Assert.True(view.Scroller.Extent.Height > measuredExtent + 1000);
            Assert.Equal(offset, view.Scroller.Offset);
            var streamingExtent = view.Scroller.Extent;
            var offscreenTool = (ToolCallRow)tools[70];
            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(offscreenTool with { Duration = TimeSpan.FromSeconds(5) })], Reset: false));
            await RenderAsync(window);
            Assert.Equal(streamingExtent, view.Scroller.Extent);
            Assert.Equal(offset, view.Scroller.Offset);
            Assert.True(view.GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("toolRow")) < tools.Length,
                DescribeTimeline(view));
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ConversationPanel_PreloadedItemsKeepStreamingHeightAndReleaseReplacedPanel()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var tools = Enumerable.Range(0, 100).Select(index => (ConversationRow)new ToolCallRow($"t{index}",
            StrongId.NewToolCallId(), "Read", JsonSerializer.SerializeToElement(new { file_path = $"file{index}.txt" }),
            ToolCallStatus.Succeeded, null, false, TimeSpan.FromMilliseconds(3), DateTimeOffset.UtcNow)).ToArray();
        var text = string.Join("\n\n", Enumerable.Repeat("Preloaded streaming answer remains measured outside the viewport.", 60));
        var assistant = new AssistantMessageRow("a1", StrongId.NewMessageId(),
            [new TextPart(StrongId.NewPartId(), text)], true, DateTimeOffset.UtcNow);
        viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(TurnPhase.Streaming), [], Reset: true,
            ResetRows: [.. tools, assistant]));
        var window = Host(view, 900, 600);
        try
        {
            var items = view.FindControl<ItemsControl>("TimelineItems")!;
            var panel = Assert.Single(view.GetVisualDescendants().OfType<ConversationPanel>());
            items.ScrollIntoView(viewModel.Items.Count - 1);
            await RenderAsync(window);
            view.Scroller.ScrollToHome();
            await RenderAsync(window);
            var extent = view.Scroller.Extent.Height;
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(assistant with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text)] })], Reset: false));
            await RenderAsync(window);
            Assert.True(view.Scroller.Extent.Height > extent + 1000);
            Assert.Equal(0, view.Scroller.Offset.Y);
            var rowIndices = panel.Children.Select(child => viewModel.Items.IndexOf((TimelineItemViewModel)child.DataContext!)).ToArray();
            Assert.Equal(rowIndices.Order(), rowIndices);
            var oldContainers = panel.Children.ToArray();
            items.ItemsPanel = new FuncTemplate<Panel?>(() => new ConversationPanel());
            await RenderAsync(window);
            var replacement = Assert.Single(view.GetVisualDescendants().OfType<ConversationPanel>());
            Assert.NotSame(panel, replacement);
            Assert.Empty(panel.Children);
            foreach (var container in oldContainers)
                Assert.DoesNotContain(container, items.GetLogicalChildren());
            Assert.NotEmpty(replacement.Children);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AutoScroll_SmallUpwardWheelIntentCancelsPendingFollow()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var text = string.Join("\n\n", Enumerable.Repeat("A long answer keeps streaming while the reader scrolls upward.", 40));
            var row = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), text)], true, DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(TurnPhase.Streaming),
                [new RowAppended(row)], Reset: false));
            await RenderAsync(window);
            var point = new Point(400, 300);
            window.MouseMove(point);
            window.MouseWheel(point, new Vector(0, 0.25));
            await RenderAsync(window);
            var readingOffset = view.Scroller.Offset;
            Assert.InRange(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - readingOffset.Y, 1, 48);
            var nested = view.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => !ReferenceEquals(scroll, view.Scroller));
            nested.RaiseEvent(new ScrollChangedEventArgs(default, new Vector(0, 10), default));
            await RenderAsync(window);
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(row with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text)] })], Reset: false));
            await RenderAsync(window);
            Assert.Equal(readingOffset, view.Scroller.Offset);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AutoScroll_CompletionHeightShrinkDoesNotDisableFollowingForNextTurn()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var text = string.Join("\n\n", Enumerable.Repeat("Streaming answer with enough content to fill the timeline.", 60));
            var row = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), text)], true, DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(TurnPhase.Streaming),
                [new RowAppended(row)], Reset: false));
            await RenderAsync(window);
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
                [new RowUpserted(row with { IsStreaming = false })], Reset: false));
            await RenderAsync(window);
            var count = view.AutoScrollCount;
            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(),
                [new RowAppended(new UserMessageRow("u2", text, DateTimeOffset.UtcNow))], Reset: false));
            await RenderAsync(window);
            Assert.Equal(count + 1, view.AutoScrollCount);
            Assert.InRange(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - view.Scroller.Offset.Y, 0, 1);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ScrollBar_DragOwnsPositionDuringStreamingAndResumesFollowingAtBottom()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var layout = Assert.Single(view.GetVisualDescendants().OfType<ConversationLayout>());
            var initialWidth = layout.Bounds.Width;
            var text = string.Join("\n\n", Enumerable.Range(0, 60).Select(index =>
                $"Paragraph {index}. Streaming output must not take control of a scrollbar while the reader is dragging it."));
            var assistant = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), text)], true, DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(TurnPhase.Streaming),
                [new RowAppended(assistant)], Reset: false));
            await RenderAsync(window);
            Assert.Equal(initialWidth, layout.Bounds.Width);
            var bar = Assert.Single(view.Scroller.GetVisualDescendants().OfType<ScrollBar>(),
                control => control.Orientation == Orientation.Vertical && ReferenceEquals(control.TemplatedParent, view.Scroller));
            var thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
            var track = Assert.Single(bar.GetVisualDescendants().OfType<Track>());
            Assert.Equal(14, bar.Bounds.Width);
            Assert.Equal(32, thumb.MinHeight);
            Assert.True(thumb.Bounds.Height >= 32);
            Assert.Equal(bar.Bounds.Height, track.Bounds.Height);
            var surface = Assert.Single(thumb.GetVisualDescendants().OfType<Border>(),
                border => border.Name == "ThumbSurface");
            Assert.Equal(8, surface.Bounds.Width);
            await RenderAsync(window);
            var origin = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(origin));
            Assert.True(ReferenceEquals(hit, thumb) || hit.GetVisualAncestors().Contains(thumb),
                $"Expected scrollbar thumb at {origin}, hit {hit.GetType().Name}; thumb {thumb.Bounds}, scrollbar {bar.Bounds}");
            window.MouseMove(origin);
            window.MouseDown(origin, MouseButton.Left);
            Pump();
            Assert.Contains(":pressed", thumb.Classes);
            window.MouseMove(origin + new Vector(1, 0), RawInputModifiers.LeftMouseButton);
            await RenderAsync(window);
            window.MouseMove(origin, RawInputModifiers.LeftMouseButton);
            await RenderAsync(window);
            var pressedOffset = view.Scroller.Offset;
            var scrollCount = view.AutoScrollCount;

            var updated = assistant with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text)] };
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(updated)], Reset: false));
            await RenderAsync(window);
            Assert.Equal(pressedOffset, view.Scroller.Offset);
            Assert.True(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - view.Scroller.Offset.Y > 48);
            Assert.Equal(scrollCount, view.AutoScrollCount);
            window.MouseMove(origin - new Vector(0, 80), RawInputModifiers.LeftMouseButton);
            await RenderAsync(window);
            Assert.True(view.Scroller.Offset.Y < pressedOffset.Y);
            var draggedOffset = view.Scroller.Offset;
            window.MouseUp(origin - new Vector(0, 80), MouseButton.Left);
            Pump();

            viewModel.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(updated with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text + "\n\nMore output.")] })],
                Reset: false));
            await RenderAsync(window);
            Assert.Equal(draggedOffset, view.Scroller.Offset);

            var trackBottom = track.TranslatePoint(new Point(track.Bounds.Width / 2, track.Bounds.Height - 1), window)!.Value;
            var currentThumb = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseMove(currentThumb);
            window.MouseDown(currentThumb, MouseButton.Left);
            window.MouseMove(trackBottom, RawInputModifiers.LeftMouseButton);
            window.MouseUp(trackBottom, MouseButton.Left);
            await RenderAsync(window);
            Assert.InRange(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - view.Scroller.Offset.Y, 0, 1);
            viewModel.ApplyUpdate(new ProjectionUpdate(4, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(updated with { Parts = [new TextPart(StrongId.NewPartId(), text + "\n\n" + text + "\n\n" + text)] })],
                Reset: false));
            await RenderAsync(window);
            Assert.InRange(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - view.Scroller.Offset.Y, 0, 1);
            Assert.Equal(14, bar.Bounds.Width);
            Assert.Equal(8, surface.Bounds.Width);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void WorkingIndicator_FollowsTurnActivity()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 600);
        try
        {
            var bar = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("workingBar"));
            Assert.False(bar.IsVisible);
            var phases = new[]
            {
                (TurnPhase.AwaitingModel, "Waiting for model", true),
                (TurnPhase.Streaming, "Generating response", true),
                (TurnPhase.ExecutingTools, "Running tools", true),
                (TurnPhase.AwaitingPermission, "Waiting for permission", false),
                (TurnPhase.AwaitingInteraction, "Waiting for your answer", false),
                (TurnPhase.AwaitingModel, "Waiting for model", true),
            };
            long sequence = 0;
            foreach (var (phase, text, running) in phases)
            {
                var state = TestSnapshots.State(phase);
                viewModel.ApplyUpdate(new ProjectionUpdate(++sequence, state, [new StatePatched(state)], Reset: false));
                Pump();

                Assert.True(bar.IsVisible);
                Assert.Equal(running, bar.Classes.Contains("running"));
                Assert.Equal(text, Avalonia.Automation.AutomationProperties.GetName(bar));
                var label = Assert.Single(bar.GetVisualDescendants().OfType<TextBlock>());
                Assert.Equal(text, label.Text);
                Assert.Equal(14, label.FontSize);
                Assert.Equal(FontWeight.Medium, label.FontWeight);
                var dots = bar.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("workingDot")).ToArray();
                Assert.Equal(3, dots.Length);
                Assert.All(dots, dot => Assert.Equal(running, dot.IsEffectivelyVisible));
                Assert.Equal(!running, Assert.Single(bar.GetVisualDescendants().OfType<Ellipse>()).IsEffectivelyVisible);
                AssertWithin(label, bar);
            }

            foreach (var phase in new[] { TurnPhase.Idle, TurnPhase.Error })
            {
                var state = TestSnapshots.State(phase);
                viewModel.ApplyUpdate(new ProjectionUpdate(++sequence, state, [new StatePatched(state)], Reset: false));
                Pump();
                Assert.False(bar.IsVisible);
                Assert.DoesNotContain("running", bar.Classes);
            }
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task WorkingIndicator_AnimatesDistinctDotsAndStopsWhenWaitingForUser()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 540, 360);
        try
        {
            var state = TestSnapshots.State(TurnPhase.AwaitingModel);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            Pump();
            var dots = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("workingDot")).ToArray();
            Assert.Equal(3, dots.Length);
            var samples = dots.Select(_ => new List<double>()).ToArray();
            var positions = dots.Select(_ => new List<double>()).ToArray();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(40);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Pump();
                for (var index = 0; index < dots.Length; index++)
                {
                    samples[index].Add(dots[index].Opacity);
                    positions[index].Add(Assert.IsType<TranslateTransform>(dots[index].RenderTransform).Y);
                }
            }

            Assert.All(samples, values => Assert.True(values.Max() - values.Min() > 0.3,
                $"Expected a visible opacity animation, sampled {values.Min():F2} to {values.Max():F2}"));
            Assert.All(positions, values => Assert.True(values.Max() - values.Min() > 1.5,
                $"Expected a moving dot, sampled Y {values.Min():F2} to {values.Max():F2}"));
            Assert.True(samples[0].Zip(samples[1]).Any(pair => Math.Abs(pair.First - pair.Second) > 0.2),
                "Dots should animate in sequence rather than flashing together.");

            var waiting = TestSnapshots.State(TurnPhase.AwaitingPermission);
            viewModel.ApplyUpdate(new ProjectionUpdate(2, waiting, [new StatePatched(waiting)], Reset: false));
            Pump();
            await Task.Delay(80);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Pump();
            Assert.All(dots, dot =>
            {
                Assert.False(dot.IsEffectivelyVisible);
                Assert.Equal(0.4, dot.Opacity, 3);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(dot.RenderTransform).Y);
            });
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
            store.Dispose();
        }
    }

    private static Button ToolHeader(TimelineView view, ToolCallItemViewModel item) =>
        Assert.Single(view.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("toolRow") && ReferenceEquals(button.DataContext, item));

    private static void AssertHeaderDuration(Button header, string expected)
    {
        var duration = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == expected && block.IsEffectivelyVisible);
        Assert.True(duration.Bounds.Width > 0 && duration.Bounds.Height > 0);
        Assert.Equal($"Elapsed time {expected}", Avalonia.Automation.AutomationProperties.GetName(duration));
        AssertWithin(duration, header);
    }

    private static void AssertWithin(Control child, Control parent)
    {
        var translated = child.TranslatePoint(default, parent);
        Assert.True(translated.HasValue,
            $"{child.GetType().Name} is not attached to the same visual tree as {parent.GetType().Name}");
        var origin = translated.Value;
        Assert.True(origin.X >= -1 && origin.X + child.Bounds.Width <= parent.Bounds.Width + 1,
            $"{child.GetType().Name} escapes {parent.GetType().Name}: x {origin.X}, width {child.Bounds.Width}, parent {parent.Bounds.Width}");
    }

    private async Task<ColorTextBlock.Avalonia.CTextBlock> WaitForParagraphAsync(Window window, TimelineView view, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            await RenderAsync(window);
            var paragraph = view.GetVisualDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>()
                .SelectMany(viewer => viewer.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>())
                .SingleOrDefault(block => block.Text == text);
            if (paragraph is not null)
            {
                return paragraph;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Streaming paragraph did not materialize; {DescribeTimeline(view)}");
            await Task.Delay(10);
        }
    }

    private static string DescribeTimeline(TimelineView view)
    {
        var panel = Assert.Single(view.GetVisualDescendants().OfType<VirtualizingPanel>());
        var rows = string.Join(", ", panel.Children.Select(control =>
            $"{(control.DataContext as TimelineItemViewModel)?.RowId}:{control.Bounds}"));
        return $"offset {view.Scroller.Offset}, extent {view.Scroller.Extent}, viewport {view.Scroller.Viewport}, " +
            $"realized {panel.Children.Count}, rows [{rows}]";
    }

    private async Task RenderAsync(Window window)
    {
        Pump();
        var visual = ElementComposition.GetElementVisual(window)!;
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Pump();
    }

    private async Task ClickAsync(Window window, Control control)
    {
        await RenderAsync(window);
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.True(new Rect(window.ClientSize).Contains(point));
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control),
            $"Expected {control.GetType().Name} at {point}, hit {hit.GetType().Name}.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void EnsureFluentTheme()
    {
        if (Application.Current is not { } app)
        {
            return;
        }
        app.RequestedThemeVariant = ThemeVariant.Dark;
        if (!app.Styles.OfType<FluentTheme>().Any())
        {
            app.Styles.Add(new FluentTheme());
        }
    }

    private static (TimelineView View, ConversationViewModel ViewModel, ProjectionStore Store) CreateTimeline()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        var viewModel = new ConversationViewModel(runtime, store);
        var view = new TimelineView { DataContext = viewModel };
        return (view, viewModel, store);
    }

    private Window Host(TimelineView view, double width, double height)
    {
        _window = new Window { Width = width, Height = height, Content = view };
        _window.Show();
        Pump();
        return _window;
    }

    private void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        _window?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        _window?.UpdateLayout();
    }
}
