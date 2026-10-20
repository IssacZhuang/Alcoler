using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Alcoler.Workbench.Tests.Views.ShellTestApp))]

namespace Alcoler.Workbench.Tests.Views;

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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class ShellSmokeTests
{
    private static readonly string[] TokenKeys =
    [
        "AlcolerBackground",
        "AlcolerSurface",
        "AlcolerSurfaceHigh",
        "AlcolerBorder",
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

        Assert.IsType<Workbench.Views.SidebarView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
        Assert.IsType<Workbench.Views.TimelineView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.TimelineView>()));
        Assert.IsType<Workbench.Views.InteractionAreaView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.InteractionAreaView>()));
        Assert.IsType<Workbench.Views.ComposerView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.ComposerView>()));
        window.Close();
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
                var composer = window.FindControl<Workbench.Views.ComposerView>("MainComposer")!;
                var interaction = window.FindControl<Workbench.Views.InteractionAreaView>("InteractionArea")!;
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
                var question = Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.QuestionCardView>());
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
                var status = window.FindControl<Workbench.Views.ConversationStatusView>("SessionStatus")!;
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
                Assert.True(Workbench.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));

                status.ExpandedOverride = false;
                Assert.False(Workbench.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));
                status.ExpandedOverride = null;
                window.Width = 1100;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(area.Bounds.Width < 1280);
                Assert.False(status.IsExpanded);
                status.ExpandedOverride = true;
                window.UpdateLayout();
                var column = window.GetVisualDescendants().OfType<Workbench.Controls.ConversationLayout>().First();
                Assert.True(status.IsExpanded);
                Assert.False(Workbench.Controls.ConversationLayout.GetReserveStatusPanelSpace(area));
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

        foreach (var key in TokenKeys)
        {
            Assert.True(window.TryFindResource(key, ThemeVariant.Dark, out var dark), key);
            var darkBrush = Assert.IsType<SolidColorBrush>(dark);
            Assert.NotEqual(default, darkBrush.Color);

            Assert.True(window.TryFindResource(key, ThemeVariant.Light, out var light), key);
            var lightBrush = Assert.IsType<SolidColorBrush>(light);
            Assert.NotEqual(darkBrush.Color, lightBrush.Color);
        }

        window.Close();
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

        var sidebar = Assert.IsType<Workbench.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
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
    public void Sidebar_ShowsEmptyState_WhenNoSessions()
    {
        var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.IsType<Workbench.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
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

        var context = window.GetVisualDescendants().OfType<Workbench.Controls.ContextUsageView>().Single();
        var fill = context.FindControl<Workbench.Controls.Icon>("ContextRingFill")!;
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
    public void Shell_UsesCompactChromeAndPreservesSidebarToggleWidth()
    {
        using var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var shell = window.FindControl<Grid>("ShellGrid")!;
        var sidebar = window.FindControl<Workbench.Views.SidebarView>("Sidebar")!;
        var width = shell.ColumnDefinitions[0].Width.Value;

        Assert.Equal(SystemDecorations.None, window.SystemDecorations);
        Assert.Equal(48, window.FindControl<Grid>("MainCard")!.RowDefinitions[1].Height.Value);
        viewModel.ToggleSidebarCommand.Execute(null);
        Assert.False(sidebar.IsVisible);
        Assert.Equal(0, shell.ColumnDefinitions[0].Width.Value);
        viewModel.ToggleSidebarCommand.Execute(null);
        Assert.True(sidebar.IsVisible);
        Assert.Equal(width, shell.ColumnDefinitions[0].Width.Value);
        window.Close();
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
