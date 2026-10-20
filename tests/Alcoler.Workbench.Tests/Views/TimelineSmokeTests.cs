using System;
using System.Linq;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

            Assert.Contains(descendants, d => d is TextBlock { Text: "running" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "context compacted" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "compact" });

            var caret = Assert.Single(descendants.OfType<Rectangle>(),
                r => r.Classes.Contains("streamingCaret"));
            Assert.True(caret.IsVisible);
        }
        finally
        {
            window.Close();
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThinkingBlock_ToggleExpandsAndCollapsesReasoning()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 420, 400);
        try
        {
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
            [
                new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                    [new ReasoningPart(StrongId.NewPartId(), "reasoning content")],
                    IsStreaming: false, At: DateTimeOffset.UtcNow)),
            ], Reset: false));
            Pump();

            var expander = Assert.Single(view.GetVisualDescendants().OfType<Expander>());
            var toggle = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>());
            var reasoning = Assert.Single(view.GetLogicalDescendants().OfType<SelectableTextBlock>(),
                block => block.Text == "reasoning content");
            var markdown = Assert.Single(view.GetLogicalDescendants().OfType<Markdown.Avalonia.MarkdownScrollViewer>());
            Assert.False(markdown.IsVisible);
            Assert.False(expander.IsExpanded);
            Assert.DoesNotContain(reasoning, view.GetVisualDescendants());

            var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump();

            Assert.True(expander.IsExpanded);
            Assert.True(reasoning.IsEffectivelyVisible);
            Assert.True(reasoning.Bounds.Height > 0);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump();

            Assert.False(expander.IsExpanded);
            Assert.False(reasoning.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
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
            Assert.Equal(24, body.LineHeight);
            var code = Assert.Single(markdown.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CCode>());
            var link = Assert.Single(markdown.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CHyperlink>());
            Assert.True(link.IsUnderline);
            var app = Application.Current!;
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = variant;
                Pump();
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerTextPrimary")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(body.Foreground).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(body.Foreground).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(code.Foreground).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerSurfaceHigh")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(code.Background).Color);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(view.FindResource(variant, "AlcolerAccent")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(link.Foreground).Color);
            }
            app.RequestedThemeVariant = ThemeVariant.Dark;
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
            store.Dispose();
        }
    }

    [AvaloniaFact]
    public void ToolCallRow_UpsertUpdatesStatus_AndClickTogglesExpansion()
    {
        EnsureFluentTheme();
        var (view, viewModel, store) = CreateTimeline();
        var window = Host(view, 900, 1000);
        try
        {
            var input = JsonDocument.Parse("{\"command\":\"ls\"}").RootElement;
            var running = new ToolCallRow("t1", StrongId.NewToolCallId(), "Bash", input,
                ToolCallStatus.Running, Output: null, IsError: false, Duration: null,
                StartedAt: DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
                [new RowAppended(running)], Reset: false));
            Pump();

            var succeeded = new ToolCallRow("t1", running.Id, "Bash", input,
                ToolCallStatus.Succeeded,
                Output: [new ToolResultText("total 0\nfile.txt")],
                IsError: false, Duration: TimeSpan.FromMilliseconds(1500),
                StartedAt: DateTimeOffset.UtcNow);
            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(),
                [new RowUpserted(succeeded)], Reset: false));
            Pump();

            var descendants = view.GetVisualDescendants().ToList();
            Assert.Contains(descendants, d => d is TextBlock { Text: "succeeded" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "1.5s" });
            Assert.Contains(descendants, d => d is TextBlock { Text: "ls" });
            Assert.DoesNotContain(descendants, d => d is TextBlock { Text: "running" });

            var toolItem = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            Assert.False(toolItem.IsExpanded);
            Assert.DoesNotContain(descendants,
                d => d is SelectableTextBlock { Text: var t } && t == toolItem.InputJson);

            var button = Assert.Single(descendants.OfType<Button>());
            Assert.Equal(0, button.BorderThickness.Left);
            Assert.Equal(new Thickness(0, 3), button.Padding);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(toolItem.IsExpanded);
            Pump();

            descendants = view.GetVisualDescendants().ToList();
            Assert.Contains(descendants,
                d => d is SelectableTextBlock { Text: var t } && t == toolItem.InputJson);
            Assert.Contains(descendants, d => d is SelectableTextBlock { Text: "total 0\nfile.txt" });
        }
        finally
        {
            window.Close();
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

            Assert.Equal(1, view.AutoScrollCount);
            Assert.Equal(0, scroller.Offset.Y);
        }
        finally
        {
            window.Close();
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
            var streaming = TestSnapshots.State(TurnPhase.Streaming);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, streaming,
                [new StatePatched(streaming)], Reset: false));
            Pump();

            var bar = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("workingBar"));
            Assert.True(bar.IsVisible);
            Assert.Contains(view.GetVisualDescendants(), d => d is TextBlock { Text: "Streaming" });

            var idle = TestSnapshots.State();
            viewModel.ApplyUpdate(new ProjectionUpdate(2, idle, [new StatePatched(idle)], Reset: false));
            Pump();

            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
            store.Dispose();
        }
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
