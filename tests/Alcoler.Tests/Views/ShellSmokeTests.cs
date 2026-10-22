using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
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
