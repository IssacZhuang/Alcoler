using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Controls;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
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
        Assert.False(session.IsRunning);
        Assert.False(session.ShowActiveDot);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.AwaitingInteraction, null));
        Assert.Equal(SessionActivityIndicator.AwaitingConfirmation, session.Indicator);
        Assert.True(session.IsAwaitingConfirmation);
        Assert.True(session.IsBusy);
        Assert.False(session.IsRunning);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        Assert.Equal(SessionActivityIndicator.Completed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Failed));
        Assert.Equal(SessionActivityIndicator.Failed, session.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);

        sidebar.ApplyActivity(session.Id, null);
        Assert.Equal(SessionActivityIndicator.None, session.Indicator);
    }

    [Theory]
    [InlineData(null, SessionActivityIndicator.None)]
    [InlineData(TurnOutcome.Completed, SessionActivityIndicator.Completed)]
    [InlineData(TurnOutcome.Failed, SessionActivityIndicator.Failed)]
    public void ApplyActivity_BackgroundJobsDoNotMakeIdleSessionBusy(
        TurnOutcome? outcome, SessionActivityIndicator indicator)
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, outcome)
        {
            RunningJobCount = 1,
        });

        Assert.Equal(indicator, session.Indicator);
        Assert.False(session.IsBusy);
        Assert.False(session.IsRunning);
        Assert.False(session.IsAwaitingConfirmation);
    }

    [AvaloniaFact]
    public async Task SessionContextFlyout_UsesSessionDataContextForArchiveActions()
    {
        using var viewModel = await ShellSmokeTests.CreateWorkbenchAsync();
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
    public void ViewingAcknowledgesCompletionButSelectionAloneDoesNot()
    {
        var activities = new SessionActivityTracker();
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask, activities);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed));
        sidebar.SetActive(session.Id);
        Assert.Equal(SessionActivityIndicator.Completed, session.Indicator);
        activities.SetViewingSession(session.Id);
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
    public void ActivitySurvivesRefreshAndAcknowledgedCompletionDoesNotResurrect()
    {
        var activities = new SessionActivityTracker();
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask, activities);
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([session]);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.ExecutingTools, null));
        Assert.Equal(SessionActivityIndicator.Running, session.Indicator);

        var refreshed = new SessionSummaryViewModel(session.Id, "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([refreshed]);
        Assert.Equal(SessionActivityIndicator.Running, refreshed.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.AwaitingPermission, null));
        refreshed = new SessionSummaryViewModel(session.Id, "Task", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([refreshed]);
        Assert.Equal(SessionActivityIndicator.AwaitingConfirmation, refreshed.Indicator);

        sidebar.ApplyActivity(session.Id, new SessionActivitySnapshot(TurnPhase.Idle, null));
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);

        var completed = new SessionActivitySnapshot(TurnPhase.Idle, TurnOutcome.Completed)
        {
            CompletedTurnId = StrongId.NewTurnId(),
        };
        sidebar.ApplyActivity(session.Id, completed);
        activities.SetViewingSession(session.Id);
        activities.SetViewingSession(null);
        sidebar.ApplyActivity(session.Id, completed);
        Assert.Equal(SessionActivityIndicator.None, sidebar.Groups[0].Sessions[0].Indicator);
    }

    [AvaloniaFact]
    public async Task Sidebar_ShowsActiveDot_WhenTaskIsActiveWithoutIndicator()
    {
        var viewModel = await ShellSmokeTests.CreateWorkbenchAsync();
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Active", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([session]);
        viewModel.Sidebar.SetActive(session.Id);
        Assert.True(session.ShowActiveDot);

        session.Indicator = SessionActivityIndicator.Running;
        Assert.False(session.ShowActiveDot);
    }

    [AvaloniaFact]
    public async Task Sidebar_RunningIndicatorUsesThemeAccentAndKeepsTime()
    {
        using var viewModel = await ShellSmokeTests.CreateWorkbenchAsync();
        var session = new SessionSummaryViewModel(StrongId.NewSessionId(), "Active", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([session]);
        session.Indicator = SessionActivityIndicator.Running;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var sidebar = Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>());
            var spinner = Assert.Single(sidebar.GetVisualDescendants().OfType<Icon>(),
                icon => icon.Name == "PART_RunningIndicator");
            var time = Assert.Single(sidebar.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "PART_SessionTime");
            var badge = Assert.Single(sidebar.GetVisualDescendants().OfType<Border>(),
                border => border.Name == "PART_AwaitingIndicator");

            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(theme, spinner.ActualThemeVariant);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(spinner.FindResource(theme, "AlcolerAccent")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(spinner.Stroke).Color);
                Assert.True(spinner.IsVisible);
                Assert.Contains("running", spinner.Classes);
                Assert.IsType<RotateTransform>(spinner.RenderTransform);
                Assert.Equal("Running", ToolTip.GetTip(spinner));
                Assert.Equal("Running", AutomationProperties.GetName(spinner));
                Assert.True(time.IsVisible);
                Assert.Equal(session.UpdatedAtText, time.Text);
                Assert.False(badge.IsVisible);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Sidebar_ShowsWaitingBadgeInsteadOfTime()
    {
        using var viewModel = await ShellSmokeTests.CreateWorkbenchAsync();
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
            Assert.Equal(label.Text, ToolTip.GetTip(spinner));
            Assert.Equal(label.Text, AutomationProperties.GetName(spinner));
            Assert.Equal(label.Text, ToolTip.GetTip(badge));
            Assert.Equal(label.Text, AutomationProperties.GetName(badge));
        }
        finally
        {
            window.Close();
        }
    }
}
