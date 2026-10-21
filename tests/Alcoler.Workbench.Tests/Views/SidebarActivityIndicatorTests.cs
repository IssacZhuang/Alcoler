using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Controls;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Views;

public sealed class SidebarActivityIndicatorTests
{
    [Fact]
    public void ApplyActivity_MapsRunningTerminalAndIdleSnapshots()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Streaming, null));
        Assert.Equal(SessionActivityIndicator.Running, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        Assert.Equal(SessionActivityIndicator.Completed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Failed));
        Assert.Equal(SessionActivityIndicator.Failed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);

        sidebar.ApplyActivity(session.Id, null);
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);
    }

    [Fact]
    public void SetActive_AcknowledgesCompletionButKeepsFailureAndRunning()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        sidebar.SetActive(session.Id);
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Failed));
        sidebar.SetActive(null);
        sidebar.SetActive(session.Id);
        Assert.Equal(SessionActivityIndicator.Failed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Streaming, null));
        sidebar.SetActive(null);
        Assert.Equal(SessionActivityIndicator.Running, session.Indicator);
    }

    [Fact]
    public void RunningState_SurvivesRefreshButDoesNotReportCompletion()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.SyncRunningState(session.Id, new SessionActivitySnapshot(TurnPhase.ExecutingTools, null));
        Assert.Equal(SessionActivityIndicator.Running, session.Indicator);

        var refreshed = new SessionSummaryViewModel(session.Id, "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([refreshed]);
        Assert.Equal(SessionActivityIndicator.Running, refreshed.Indicator);

        sidebar.SyncRunningState(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);

        sidebar.SyncRunningState(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);
    }

    [AvaloniaFact]
    public void Sidebar_RendersRunningSpinnerCompletionAndFailureIndicators()
    {
        var viewModel = ShellSmokeTests.CreateWorkbench();
        var running = new SessionSummaryViewModel(StrongId.NewSessionId(), "Running", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        var completed = new SessionSummaryViewModel(StrongId.NewSessionId(), "Completed", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        var failed = new SessionSummaryViewModel(StrongId.NewSessionId(), "Failed", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([running, completed, failed]);
        viewModel.Sidebar.ApplyActivity(running.Id, new SessionActivitySnapshot(TurnPhase.Streaming, null));
        viewModel.Sidebar.ApplyActivity(completed.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        viewModel.Sidebar.ApplyActivity(failed.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Failed));
        viewModel.Sidebar.SetActive(running.Id);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var sidebar = Assert.IsType<Alcoler.Workbench.Views.SidebarView>(
                Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Workbench.Views.SidebarView>()));
            var spinners = sidebar.GetVisualDescendants().OfType<Icon>()
                .Where(icon => icon.Name == "PART_RunningIndicator").ToList();
            Assert.Equal(3, spinners.Count);
            var visibleSpinner = Assert.Single(spinners, spinner => spinner.IsVisible);
            Assert.Contains("running", visibleSpinner.Classes);
            Assert.IsType<Avalonia.Media.RotateTransform>(visibleSpinner.RenderTransform);
            Assert.IsNotType<Avalonia.Media.Transformation.TransformOperations>(visibleSpinner.RenderTransform);

            var completedDots = sidebar.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name == "PART_CompletedIndicator").ToList();
            Assert.Equal(3, completedDots.Count);
            Assert.Single(completedDots, dot => dot.IsVisible);
            Assert.Equal("Completed", Avalonia.Automation.AutomationProperties.GetName(
                completedDots.Single(dot => dot.IsVisible)));

            var failedDots = sidebar.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name == "PART_FailedIndicator").ToList();
            Assert.Equal(3, failedDots.Count);
            Assert.Single(failedDots, dot => dot.IsVisible);

            var activeDots = sidebar.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name == "PART_ActiveIndicator").ToList();
            Assert.Equal(3, activeDots.Count);
            Assert.DoesNotContain(activeDots, dot => dot.IsVisible);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Sidebar_ShowsActiveDot_WhenTaskIsActiveWithoutIndicator()
    {
        var viewModel = ShellSmokeTests.CreateWorkbench();
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Active", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([session]);
        viewModel.Sidebar.SetActive(session.Id);
        Assert.True(session.ShowActiveDot);

        session.Indicator = SessionActivityIndicator.Running;
        Assert.False(session.ShowActiveDot);
    }
}
