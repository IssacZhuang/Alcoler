using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Alcoler.Tests.Views.ShellTestApp))]

namespace Alcoler.Tests.Views;

// Headless controls must use the production styles to exercise template overrides.
public sealed class ShellTestApp : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var app = new App();
        app.Initialize();
        var resources = app.Resources;
        app.Resources = new ResourceDictionary();
        Resources = resources;
        var styles = app.Styles.ToArray();
        app.Styles.Clear();
        foreach (var style in styles)
        {
            Styles.Add(style);
        }
    }

    public static Avalonia.AppBuilder BuildAvaloniaApp() =>
        Avalonia.AppBuilder.Configure<ShellTestApp>()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

public class ShellSmokeTests
{
    private static readonly string[] TokenKeys =
    [
        "AlcolerBackground",
        "AlcolerSurface",
        "AlcolerSurfaceHigh",
        "AlcolerBorder",
        "AlcolerBorderHover",
        "AlcolerTextPrimary",
        "AlcolerTextSecondary",
        "AlcolerAccent",
        "AlcolerAccentText",
        "AlcolerSuccess",
        "AlcolerWarning",
        "AlcolerError",
        "AlcolerInfo",
    ];

    [AvaloniaFact]
    public void MainWindow_Constructs_And_HostsAllRegions()
    {
        var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();

        Assert.IsType<Alcoler.Views.SidebarView>(Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>()));
        Assert.IsType<Alcoler.Views.TimelineView>(Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.TimelineView>()));
        Assert.IsType<Alcoler.Views.InteractionAreaView>(Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.InteractionAreaView>()));
        Assert.IsType<Alcoler.Views.ComposerView>(Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.ComposerView>()));
        window.Close();
    }

    [AvaloniaFact]
    public void DraftScreen_DoesNotShowStatusCardBeforeTheConversationStarts()
    {
        var runtime = new FakeAgentRuntime();
        using var viewModel = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var status = window.FindControl<Alcoler.Views.ConversationStatusView>("SessionStatus")!;
            Assert.Null(viewModel.Active);
            Assert.True(viewModel.IsDraft);
            Assert.False(status.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Question_ReplacesComposerWithoutDiscardingDraft_AndRestoresItAfterResolution()
    {
        var (viewModel, store) = CreateWorkbenchWithoutPump();
        using (viewModel)
        using (store)
        {
            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var composer = window.FindControl<Alcoler.Views.ComposerView>("MainComposer")!;
                var interaction = window.FindControl<Alcoler.Views.InteractionAreaView>("InteractionArea")!;
                Assert.False(interaction.IsVisible);
                Assert.True(double.IsPositiveInfinity(interaction.MaxHeight));
                viewModel.Active!.Composer.Draft = "Keep this unfinished message";
                var request = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Input required",
                [
                    new("Storage", "Which storage should be used?", [new("SQLite"), new("Files")]),
                ]);
                var questionState = TestSnapshots.State(TurnPhase.AwaitingInteraction) with { PendingInteraction = request };
                viewModel.Active.ApplyUpdate(new ProjectionUpdate(2, questionState, [new StatePatched(questionState)], false));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.False(composer.IsVisible);
                Assert.True(interaction.IsVisible);
                Assert.Empty(window.OwnedWindows);
                var question = Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.QuestionCardView>());
                Assert.True(question.Bounds.Height > 0);
                Assert.True(question.Bounds.Height <= interaction.MaxHeight);
                Assert.Equal("Keep this unfinished message", viewModel.Active.Composer.Draft);

                var idle = TestSnapshots.State();
                viewModel.Active.ApplyUpdate(new ProjectionUpdate(3, idle, [new StatePatched(idle)], false));
                Dispatcher.UIThread.RunJobs();
                Assert.True(composer.IsVisible);
                Assert.False(interaction.IsVisible);
                Assert.True(double.IsPositiveInfinity(interaction.MaxHeight));
                Assert.Equal("Keep this unfinished message", viewModel.Active.Composer.Draft);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Status_UsesConversationWidthAndReservesSpaceOnlyWhenExpandedAndWide()
    {
        var (viewModel, store) = CreateWorkbenchWithoutPump();
        using (viewModel)
        using (store)
        {
            var window = new MainWindow(viewModel) { Width = 1650 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var area = window.FindControl<Grid>("ConversationArea")!;
                var status = window.FindControl<Alcoler.Views.ConversationStatusView>("SessionStatus")!;
                Assert.False(status.IsVisible);
                var todo = new ToolCallRow("todo", StrongId.NewToolCallId(), "TodoWrite",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "Review UI", status = "in_progress" } } }),
                    ToolCallStatus.Succeeded, [], false, TimeSpan.Zero, DateTimeOffset.UtcNow);
                viewModel.Active!.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(), [new RowAppended(todo)], false));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(status.IsVisible);
                Assert.Equal(area.Bounds.Width, status.ConversationWidth);
                Assert.True(status.IsExpanded);
                Assert.True(Alcoler.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));

                status.ExpandedOverride = false;
                Assert.False(Alcoler.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));
                status.ExpandedOverride = null;
                window.Width = 1100;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(area.Bounds.Width < 1280);
                Assert.False(status.IsExpanded);
                status.ExpandedOverride = true;
                window.UpdateLayout();
                var column = window.GetVisualDescendants().OfType<Alcoler.Controls.ConversationLayout>().First();
                Assert.True(status.IsExpanded);
                Assert.False(Alcoler.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));
                Assert.True(column.Bounds.Width < 1280);
                Assert.Equal(area.Bounds.Width, status.ConversationWidth);
                Assert.True(status.MaxHeight <= 512);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Theme_TokensResolveInDarkAndLight()
    {
        var window = new Window();
        window.Show();

        try
        {
            foreach (var key in TokenKeys)
            {
                Assert.True(window.TryFindResource(key, ThemeVariant.Dark, out var dark), key);
                var darkBrush = Assert.IsType<SolidColorBrush>(dark);
                Assert.True(window.TryFindResource(key, ThemeVariant.Light, out var light), key);
                var lightBrush = Assert.IsType<SolidColorBrush>(light);
                if (key is "AlcolerBorder" or "AlcolerBorderHover")
                {
                    Assert.Equal(0, darkBrush.Color.A);
                    Assert.Equal(darkBrush.Color, lightBrush.Color);
                }
                else
                {
                    Assert.Equal(255, darkBrush.Color.A);
                    Assert.Equal(255, lightBrush.Color.A);
                    Assert.NotEqual(darkBrush.Color, lightBrush.Color);
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Typography_UsesSourceGitFamiliesAndZCodeSizesInBothThemes()
    {
        using var viewModel = CreateWorkbench();
        viewModel.Sidebar.ApplySummaries([
            new SessionSummaryViewModel(NewId(), "Typography session", @"C:\ws\alpha", DateTimeOffset.UtcNow),
        ]);
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(14, window.FontSize);
                Assert.Equal("Inter", window.FontFamily.Name);
                foreach (var (key, size) in new (string, double)[]
                {
                    ("AlcolerFontXs", 10), ("AlcolerFontSmall", 12), ("AlcolerFontCaption", 13),
                    ("AlcolerFontBase", 14), ("AlcolerFontLarge", 16), ("AlcolerFontXLarge", 18),
                })
                {
                    Assert.Equal(size, Assert.IsType<double>(window.FindResource(theme, key)));
                }
                var composer = window.FindControl<Alcoler.Views.ComposerView>("MainComposer")!;
                var input = composer.FindControl<TextBox>("InputBox")!;
                Assert.Equal(14, input.FontSize);
                Assert.Equal(20, input.LineHeight);
                Assert.Equal("JetBrains Mono", input.FontFamily.Name);
                Assert.Equal(14, composer.FindControl<TextBlock>("ModelLabel")!.FontSize);
                Assert.Equal(14, composer.FindControl<TextBlock>("StatusTriggerText")!.FontSize);
                var title = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Classes.Contains("sessionTitle"));
                var time = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Classes.Contains("sessionTime"));
                Assert.Equal(14, title.FontSize);
                Assert.Equal("JetBrains Mono", title.FontFamily.Name);
                Assert.Equal(12, time.FontSize);
                var terminal = Assert.Single(window.GetLogicalDescendants().OfType<Alcoler.Views.TerminalView>());
                var surface = terminal.FindControl<Alcoler.Views.TerminalSurface>("Surface")!;
                Assert.Equal(13, surface.FontSize);
                Assert.Equal("JetBrains Mono", surface.FontFamily.Name);
                foreach (var key in new[] { "AlcolerFontPrimary", "AlcolerFontMonospace" })
                {
                    var family = Assert.IsType<FontFamily>(window.FindResource(theme, key));
                    Assert.Equal("JetBrains Mono", family.Name);
                    foreach (var (weight, style) in new[]
                    {
                        (FontWeight.Normal, FontStyle.Normal), (FontWeight.Bold, FontStyle.Normal),
                        (FontWeight.Normal, FontStyle.Italic),
                    })
                    {
                        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family, style, weight), out var glyph));
                        Assert.Equal("JetBrains Mono", glyph.FamilyName);
                        Assert.Equal(weight, glyph.Weight);
                        Assert.Equal(style, glyph.Style);
                        Assert.Equal(FontSimulations.None, glyph.FontSimulations);
                        Assert.True(glyph.TryGetTable(0x6E616D65u, out var nameTable));
                        Assert.NotEmpty(nameTable);
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Sidebar_RendersGroupsSessionsAndActiveState()
    {
        var viewModel = CreateWorkbench();
        var alphaA = new SessionSummaryViewModel(NewId(), "Session A", @"C:\ws\alpha", DateTimeOffset.UtcNow.AddMinutes(-5));
        var alphaB = new SessionSummaryViewModel(NewId(), "Session B", @"C:\ws\alpha", DateTimeOffset.UtcNow.AddMinutes(-1));
        var betaC = new SessionSummaryViewModel(NewId(), "Session C", @"C:\ws\beta", DateTimeOffset.UtcNow.AddHours(-2));
        viewModel.Sidebar.ApplySummaries([alphaA, alphaB, betaC]);
        viewModel.Sidebar.SetActive(alphaB.Id);

        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.IsType<Alcoler.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>()));
        var buttons = sidebar.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("sessionItem"))
            .ToList();
        Assert.Equal(3, buttons.Count);

        var titles = buttons
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("sessionTitle")).Text)
            .ToList();
        Assert.Equal(new[] { "Session B", "Session A", "Session C" }, titles);

        var indicators = sidebar.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "PART_ActiveIndicator")
            .ToList();
        Assert.Equal(3, indicators.Count);
        Assert.Single(indicators, i => i.IsVisible);

        var empty = sidebar.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => t.Name == "PART_SidebarEmpty");
        Assert.False(empty.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_ProjectNewTaskButton_StaysInsideViewport_WithLongSessionTitles()
    {
        var viewModel = CreateWorkbench();
        var longTitle = string.Join(' ', Enumerable.Repeat("Aligning long session titles", 8));
        viewModel.Sidebar.ApplySummaries([
            new SessionSummaryViewModel(NewId(), longTitle, @"C:\ws\alpha", DateTimeOffset.UtcNow),
        ]);

        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>());
        var newTask = Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("projectNewTask"));
        var origin = newTask.TranslatePoint(default, sidebar)!.Value;

        Assert.True(origin.X >= 0);
        Assert.True(origin.X + newTask.Bounds.Width <= sidebar.Bounds.Width,
            $"New-task button spans {origin.X}..{origin.X + newTask.Bounds.Width} in a {sidebar.Bounds.Width}px sidebar.");
        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_ShowsEmptyState_WhenNoSessions()
    {
        var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.IsType<Alcoler.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>()));
        Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains("sessionItem"));
        var empty = sidebar.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => t.Name == "PART_SidebarEmpty");
        Assert.True(empty.IsVisible);
        Assert.Equal("No sessions yet", empty.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void Header_MeterAndPhase_TrackStatePatch()
    {
        var (viewModel, store) = CreateWorkbenchWithoutPump();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var context = window.GetVisualDescendants().OfType<Alcoler.Controls.ContextUsageView>().Single();
        var fill = context.FindControl<Alcoler.Controls.Icon>("ContextRingFill")!;
        var phase = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_PhaseText");

        Assert.False(context.IsVisible);

        var low = TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 60_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(2, low, [new StatePatched(low)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Streaming", phase.Text);
        Assert.True(context.IsVisible);
        Assert.Equal(.3, Assert.IsType<ContextUsageViewModel>(context.DataContext).Fraction);
        Assert.Equal(14, fill.Width);
        var lowBrush = Assert.IsType<SolidColorBrush>(fill.Stroke);

        var mid = TestSnapshots.State(TurnPhase.ExecutingTools, contextUsageTokens: 120_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(3, mid, [new StatePatched(mid)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ExecutingTools", phase.Text);
        Assert.Equal(.6, Assert.IsType<ContextUsageViewModel>(context.DataContext).Fraction);
        Assert.Equal(lowBrush.Color, Assert.IsType<SolidColorBrush>(fill.Stroke).Color);

        var hot = TestSnapshots.State(TurnPhase.Error, contextUsageTokens: 180_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(4, hot, [new StatePatched(hot)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Error", phase.Text);
        Assert.Equal(.9, Assert.IsType<ContextUsageViewModel>(context.DataContext).Fraction);
        Assert.Equal(lowBrush.Color, Assert.IsType<SolidColorBrush>(fill.Stroke).Color);

        window.Close();
        viewModel.Dispose();
        store.Dispose();
    }

    [AvaloniaFact]
    public async Task Shell_UsesCompactChromeAndPreservesSidebarToggleWidth()
    {
        using var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var shell = window.FindControl<Grid>("ShellGrid")!;
            var sidebar = window.FindControl<Alcoler.Views.SidebarView>("Sidebar")!;
            var width = shell.ColumnDefinitions[0].Width.Value;

            Assert.Equal(SystemDecorations.None, window.SystemDecorations);
            Assert.Equal(48, window.FindControl<Grid>("MainCard")!.RowDefinitions[1].Height.Value);
            viewModel.ToggleSidebarCommand.Execute(null);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(width, shell.ColumnDefinitions[0].Width.Value);
            await WaitUntilAsync(() => shell.ColumnDefinitions[0].Width.Value == 0);
            Assert.False(sidebar.IsVisible);
            viewModel.ToggleSidebarCommand.Execute(null);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(0, shell.ColumnDefinitions[0].Width.Value);
            await WaitUntilAsync(() => shell.ColumnDefinitions[0].Width.Value == width);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(width, shell.ColumnDefinitions[0].Width.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SidePane_AnimatesOpenAndClosed()
    {
        using var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel) { Width = 1400, Height = 900 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var shell = window.FindControl<Grid>("ShellGrid")!;
            var host = window.FindControl<Border>("DocumentsHost")!;
            var column = shell.ColumnDefinitions[4];
            Assert.False(host.IsVisible);
            Assert.Equal(0, column.Width.Value);

            viewModel.ToggleSidePaneCommand.Execute(null);
            Assert.True(host.IsVisible);
            Assert.Equal(0, column.Width.Value);
            await WaitUntilAsync(() => !window.IsPaneAnimationActive);
            var openWidth = column.Width.Value;
            Assert.True(openWidth >= 240);
            Assert.True(host.IsVisible);

            viewModel.ToggleSidePaneCommand.Execute(null);
            Assert.True(host.IsVisible);
            Assert.Equal(openWidth, column.Width.Value);
            await WaitUntilAsync(() => !window.IsPaneAnimationActive);
            Assert.Equal(0, column.Width.Value);
            Assert.False(host.IsVisible);
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "Pane animation did not settle within 5 seconds.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void PrimaryButton_ExplicitTextUsesContrastingThemeForeground()
    {
        var label = new TextBlock { Text = "Add provider" };
        var button = new Button { Content = label };
        button.Classes.Add("primary");
        var window = new Window { Content = button };
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.TryFindResource("AlcolerBrandText", theme, out var foreground));
            Assert.Equal(Assert.IsType<SolidColorBrush>(foreground).Color,
                Assert.IsType<SolidColorBrush>(label.Foreground).Color);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_RefreshPreservesCollapsedProjects()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(NewId(), "Session", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);
        sidebar.Groups[0].IsExpanded = false;
        sidebar.ApplySummaries([session]);
        Assert.False(sidebar.Groups[0].IsExpanded);
    }

    internal static WorkbenchViewModel CreateWorkbench()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var viewModel = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        viewModel.OpenSessionAsync(sessionId).GetAwaiter().GetResult();
        return viewModel;
    }

    /// <summary>Workbench whose projection store is never started, so state changes are driven only by direct patches.</summary>
    private static (WorkbenchViewModel ViewModel, ProjectionStore Store) CreateWorkbenchWithoutPump()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        var viewModel = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        viewModel.Active = new ConversationViewModel(runtime, store);
        return (viewModel, store);
    }

    private static SessionId NewId() => new(Guid.NewGuid().ToString("N"));
}
