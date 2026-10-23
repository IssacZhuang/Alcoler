using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Controls;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

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

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.AwaitingPermission, null));
        Assert.Equal(SessionActivityIndicator.AwaitingConfirmation, session.Indicator);
        Assert.True(session.IsAwaitingConfirmation);
        Assert.True(session.IsBusy);
        Assert.False(session.ShowActiveDot);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.AwaitingInteraction, null));
        Assert.Equal(SessionActivityIndicator.AwaitingConfirmation, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        Assert.Equal(SessionActivityIndicator.Completed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Failed));
        Assert.Equal(SessionActivityIndicator.Failed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);

        sidebar.ApplyActivity(session.Id, null);
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);
    }

    [AvaloniaFact]
    public async Task SessionContextFlyout_UsesSessionDataContextForArchiveActions()
    {
        using var viewModel = ShellSmokeTests.CreateWorkbench();
        var active = new SessionSummaryViewModel(
            StrongId.NewSessionId(), "Active", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        var archived = new SessionSummaryViewModel(
            StrongId.NewSessionId(), "Archived", @"C:\ws\alpha", DateTimeOffset.UtcNow, isArchived: true);
        viewModel.Sidebar.ApplySummaries([active, archived]);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var sidebar = Assert.IsType<Alcoler.Views.SidebarView>(
                Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>()));
            var rows = sidebar.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("sessionItem")
                    && button.DataContext is SessionSummaryViewModel)
                .ToList();
            Assert.Equal(2, rows.Count);

            await AssertFlyoutAsync(
                rows.Single(row => ((SessionSummaryViewModel)row.DataContext!).Id == active.Id),
                showArchive: true);
            await AssertFlyoutAsync(
                rows.Single(row => ((SessionSummaryViewModel)row.DataContext!).Id == archived.Id),
                showArchive: false);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task AssertFlyoutAsync(Button row, bool showArchive)
    {
        var summary = Assert.IsType<SessionSummaryViewModel>(row.DataContext);
        var menu = Assert.IsType<MenuFlyout>(row.ContextFlyout);
        menu.ShowAt(row);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var archive = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Archive"));
            var unarchive = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Unarchive"));
            Assert.Same(summary, archive.DataContext);
            Assert.Same(summary, unarchive.DataContext);
            Assert.Equal(showArchive, archive.IsVisible);
            Assert.Equal(!showArchive, unarchive.IsVisible);
        }
        finally
        {
            menu.Hide();
        }
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
    public void ActiveState_SurvivesRefreshButDoesNotReportCompletion()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.SyncActiveState(session.Id, new SessionActivitySnapshot(TurnPhase.ExecutingTools, null));
        Assert.Equal(SessionActivityIndicator.Running, session.Indicator);

        var refreshed = new SessionSummaryViewModel(session.Id, "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([refreshed]);
        Assert.Equal(SessionActivityIndicator.Running, refreshed.Indicator);

        sidebar.SyncActiveState(session.Id, new SessionActivitySnapshot(TurnPhase.AwaitingPermission, null));
        refreshed = new SessionSummaryViewModel(session.Id, "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([refreshed]);
        Assert.Equal(SessionActivityIndicator.AwaitingConfirmation, refreshed.Indicator);

        sidebar.SyncActiveState(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);

        sidebar.SyncActiveState(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);
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

    [AvaloniaFact]
    public async Task Sidebar_ShowsWaitingBadgeInsteadOfTime()
    {
        using var viewModel = ShellSmokeTests.CreateWorkbench();
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Active", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([session]);
        session.Indicator = SessionActivityIndicator.AwaitingConfirmation;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var sidebar = Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>());
            var badge = Assert.Single(sidebar.GetVisualDescendants().OfType<Border>(),
                border => border.Name == "PART_AwaitingIndicator");
            var time = Assert.Single(sidebar.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "PART_SessionTime");
            var spinner = Assert.Single(sidebar.GetVisualDescendants().OfType<Icon>(),
                icon => icon.Name == "PART_RunningIndicator");
            var label = Assert.Single(badge.GetVisualDescendants().OfType<TextBlock>());

            Assert.True(badge.IsVisible);
            Assert.False(time.IsVisible);
            Assert.True(spinner.IsVisible);
            Assert.Equal("Waiting for confirmation", label.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
