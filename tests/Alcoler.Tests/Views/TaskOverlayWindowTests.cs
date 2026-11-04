using System.Reflection;
using Alcoler.Controls;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Localization;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

[Collection("Global localization")]
public sealed class TaskOverlayWindowTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh")]
    public async Task Header_ContainsOnlyBrandWithoutTaskCounts(string language)
    {
        var previousLanguage = Loc.Language;
        Loc.SetLanguage(language);
        try
        {
            using var harness = new AppHarness();
            harness.Overlay.ShowNear(harness.Main);
            var running = harness.RegisterTask("running");
            var waiting = harness.RegisterTask("waiting", TurnPhase.AwaitingPermission)
                with { IsAwaitingConfirmation = true };
            IReadOnlyList<SessionActivitySnapshot>[] snapshots = [[], [running, waiting], []];
            foreach (var tasks in snapshots)
            {
                harness.Runtime.Tasks = tasks;
                await RenderAsync(harness.Overlay);

                Assert.Equal(tasks.Count, harness.Workbench.TaskOverlay.Sessions.Count);
                Assert.Equal(Loc.Get("task_overlay_title"), harness.Overlay.Title);
                var header = Named<Grid>(harness.Overlay, "OverlayHeader");
                Assert.Equal(Loc.Get("task_overlay_title"), Assert.Single(header.GetVisualDescendants().OfType<TextBlock>()).Text);
            }
        }
        finally
        {
            Loc.SetLanguage(previousLanguage);
        }
    }

    [AvaloniaTheory]
    [InlineData(TurnPhase.AwaitingModel, false)]
    [InlineData(TurnPhase.ExecutingTools, false)]
    [InlineData(TurnPhase.AwaitingPermission, true)]
    [InlineData(TurnPhase.AwaitingInteraction, true)]
    [InlineData(TurnPhase.Idle, false)]
    [InlineData(TurnPhase.Idle, true)]
    public async Task SessionList_RendersOnlyTwoRowSessionSummaryAndReturnsToEmptyState(TurnPhase phase, bool waiting)
    {
        var task = TaskOverlayViewModelTests.TaskSnapshot("rendered", phase,
            DateTimeOffset.UtcNow.AddMinutes(-2), waiting) with { RunningJobCount = 2, RunningSubagentCount = 1 };
        var tracker = TaskOverlayViewModelTests.CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        var window = new TaskOverlayWindow(model);
        window.Show();
        try
        {
            await RenderAsync(window);
            var row = Assert.Single(model.Sessions);
            var button = SessionButton(window, task.SessionId);
            Assert.False(Named<TextBlock>(window, "EmptyState").IsVisible);
            var grid = Assert.IsType<Grid>(button.Content);
            Assert.Equal(2, grid.RowDefinitions.Count);
            var blocks = button.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(new[] { row.Title, row.DurationText, row.WorkspaceName }, blocks.Select(block => block.Text));
            Assert.Equal(new[] { 0, 0, 1 }, blocks.Select(block => Grid.GetRow(block)));
            Assert.Equal(new[] { 1, 2, 1 }, blocks.Select(block => Grid.GetColumn(block)));
            Assert.All(blocks, block => Assert.Same(grid, block.GetVisualParent()));
            var status = Assert.IsType<Grid>(Named<Icon>(button, "PART_RunningIndicator").GetVisualParent());
            Assert.Same(grid, status.GetVisualParent());
            Assert.Equal(0, Grid.GetRow(status));
            Assert.Equal(0, Grid.GetColumn(status));
            AssertStatusIndicators(button, busy: true, completed: false, failed: false);
            Assert.Equal(task.WorkspaceRoot, ToolTip.GetTip(button));

            tracker.ApplyActivity(task.SessionId, task with { Title = "Updated task", RunningJobCount = 1 });
            await RenderAsync(window);
            Assert.Same(row, Assert.Single(model.Sessions));
            Assert.Contains(SessionButton(window, task.SessionId).GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Updated task");

            tracker.Refresh([]);
            await RenderAsync(window);
            Assert.True(Named<TextBlock>(window, "EmptyState").IsEffectivelyVisible);
            Assert.Equal(Loc.Get("task_overlay_empty"), Named<TextBlock>(window, "EmptyState").Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("session"));
        }
        finally
        {
            window.Shutdown();
        }
    }

    [AvaloniaTheory]
    [InlineData(TurnPhase.ProcessingInput)]
    [InlineData(TurnPhase.AwaitingModel)]
    [InlineData(TurnPhase.Streaming)]
    [InlineData(TurnPhase.ExecutingTools)]
    [InlineData(TurnPhase.AwaitingPermission)]
    [InlineData(TurnPhase.AwaitingInteraction)]
    [InlineData(TurnPhase.Completing)]
    public async Task StatusIndicators_ActiveAndWaitingPhasesAnimateTheSidebarAccentRing(TurnPhase phase)
    {
        var waiting = phase is TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction;
        var task = TaskOverlayViewModelTests.TaskSnapshot("animated", phase, DateTimeOffset.UtcNow, waiting)
            with { LastOutcome = TurnOutcome.Failed };
        var model = new TaskOverlayViewModel(TaskOverlayViewModelTests.CreateTracker(task));
        var window = new TaskOverlayWindow(model);
        window.Show();
        try
        {
            await RenderAsync(window);
            var button = SessionButton(window, task.SessionId);
            AssertStatusIndicators(button, busy: true, completed: false, failed: false);
            var spinner = Named<Icon>(button, "PART_RunningIndicator");
            foreach (var name in new[] { "icon", "small", "sessionSpinner", "running" })
                Assert.Contains(name, spinner.Classes);
            Assert.Equal(new Size(16, 16), Assert.IsType<Grid>(spinner.GetVisualParent()).Bounds.Size);
            Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), spinner.RenderTransformOrigin);
            Assert.NotNull(spinner.Data);
            Assert.Equal(Geometry.Parse("M12 3a9 9 0 1 0 9 9").ToString(), spinner.Data.ToString());
            AssertThemeColor(window, spinner.Stroke, "AlcolerAccent");
            var rotation = Assert.IsType<RotateTransform>(spinner.RenderTransform);
            var angle = rotation.Angle;
            await Task.Delay(80);
            await RenderAsync(window);
            Assert.NotEqual(angle, rotation.Angle);

            if (phase == TurnPhase.Streaming)
            {
                await Task.Delay(950);
                await RenderAsync(window);
                angle = rotation.Angle;
                await Task.Delay(80);
                await RenderAsync(window);
                Assert.NotEqual(angle, rotation.Angle);
            }
        }
        finally
        {
            window.Shutdown();
        }
    }

    [AvaloniaFact]
    public async Task StatusIndicators_InjectedPresentationTransitionsReuseRowAndTrackLightAndDarkThemes()
    {
        var task = TaskOverlayViewModelTests.TaskSnapshot("status-transition", startedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        SessionActivitySnapshot Finished(TurnOutcome outcome, string turnId, TurnPhase phase = TurnPhase.Idle) => task with
        {
            Phase = phase,
            LastOutcome = outcome,
            HasActiveWork = false,
            ActiveWorkStartedAt = null,
            FinishedAt = task.StartedAt!.Value.AddSeconds(55),
            CompletedTurnId = new TurnId(turnId),
        };
        var tracker = TaskOverlayViewModelTests.CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        var window = new TaskOverlayWindow(model);
        window.Show();
        try
        {
            await RenderAsync(window);
            var row = Assert.Single(model.Sessions);
            var button = SessionButton(window, task.SessionId);
            var spinner = Named<Icon>(button, "PART_RunningIndicator");
            var completed = Named<Border>(button, "PART_CompletedIndicator");
            var failed = Named<Border>(button, "PART_FailedIndicator");
            foreach (var dot in new[] { completed, failed })
            {
                Assert.Equal(6d, dot.Width);
                Assert.Equal(6d, dot.Height);
                Assert.Equal(new CornerRadius(3), dot.CornerRadius);
                Assert.Same(spinner.GetVisualParent(), dot.GetVisualParent());
            }
            var rotation = Assert.IsType<RotateTransform>(spinner.RenderTransform);
            List<(Color Accent, Color Success, Color Error)> colors = [];
            (SessionActivitySnapshot Snapshot, bool Busy, bool Completed, bool Failed)[] states =
            [
                (task with { LastOutcome = TurnOutcome.Completed }, true, false, false),
                (Finished(TurnOutcome.Completed, "first-completed"), false, true, false),
                (task with { LastOutcome = TurnOutcome.Failed }, true, false, false),
                (Finished(TurnOutcome.Failed, "first-failed"), false, false, true),
                (Finished(TurnOutcome.Completed, "error-finish", TurnPhase.Error) with
                    { HasActiveWork = true, RunningJobCount = 1 }, false, false, true),
                (task with { Phase = TurnPhase.Idle, LastOutcome = TurnOutcome.Completed,
                    HasActiveWork = true }, true, false, false),
                (Finished(TurnOutcome.Completed, "second-completed"), false, true, false),
            ];
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                tracker.ApplyActivity(task.SessionId, task);
                await RenderAsync(window);
                colors.Add((AssertThemeColor(window, spinner.Stroke, "AlcolerAccent"),
                    AssertThemeColor(window, completed.Background, "AlcolerSuccess"),
                    AssertThemeColor(window, failed.Background, "AlcolerError")));
                Assert.Equal(theme, button.ActualThemeVariant);
                foreach (var state in states)
                {
                    tracker.ApplyActivity(task.SessionId, state.Snapshot with
                    {
                        CompletedTurnId = state.Snapshot.CompletedTurnId is { } turnId
                            ? new TurnId($"{theme}-{turnId.Value}") : null,
                    });
                    await RenderAsync(window);

                    Assert.Same(row, Assert.Single(model.Sessions));
                    Assert.Same(button, SessionButton(window, task.SessionId));
                    Assert.Same(spinner, Named<Icon>(button, "PART_RunningIndicator"));
                    Assert.Same(completed, Named<Border>(button, "PART_CompletedIndicator"));
                    Assert.Same(failed, Named<Border>(button, "PART_FailedIndicator"));
                    AssertStatusIndicators(button, state.Busy, state.Completed, state.Failed);
                    if (state.Busy)
                        AssertThemeColor(window, spinner.Stroke, "AlcolerAccent");
                    if (state.Completed)
                        AssertThemeColor(window, completed.Background, "AlcolerSuccess");
                    if (state.Failed)
                        AssertThemeColor(window, failed.Background, "AlcolerError");
                    var angle = rotation.Angle;
                    await Task.Delay(80);
                    await RenderAsync(window);
                    if (state.Busy)
                        Assert.NotEqual(angle, rotation.Angle);
                    else
                        Assert.Equal(angle, rotation.Angle);
                }
            }
            Assert.NotEqual(colors[0].Accent, colors[1].Accent);
            Assert.NotEqual(colors[0].Success, colors[1].Success);
            Assert.NotEqual(colors[0].Error, colors[1].Error);

            tracker.Remove(task.SessionId);
            await RenderAsync(window);
            Assert.Empty(model.Sessions);
            Assert.True(Named<TextBlock>(window, "EmptyState").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), control => control.Classes.Contains("session"));
        }
        finally
        {
            window.Shutdown();
        }
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(TurnOutcome.Cancelled)]
    [InlineData(TurnOutcome.ForcedStop)]
    public async Task SessionList_ExcludesIdleCancelledForcedAndEmptyResults(TurnOutcome? outcome)
    {
        var task = TaskOverlayViewModelTests.TerminalSnapshot("excluded", outcome);
        var model = new TaskOverlayViewModel(TaskOverlayViewModelTests.CreateTracker(task));
        var window = new TaskOverlayWindow(model);
        window.Show();
        try
        {
            await RenderAsync(window);

            Assert.Empty(model.Sessions);
            Assert.True(Named<TextBlock>(window, "EmptyState").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("session"));
        }
        finally
        {
            window.Shutdown();
        }
    }

    [AvaloniaTheory]
    [InlineData(WindowState.Normal, false, true)]
    [InlineData(WindowState.Maximized, false, true)]
    [InlineData(WindowState.Normal, true, false)]
    [InlineData(WindowState.Maximized, true, false)]
    [InlineData(WindowState.Normal, true, true)]
    [InlineData(WindowState.Maximized, true, true)]
    public async Task HeaderDoubleClick_RequestsMainOnceAndRestoresItsPreviousStateWithoutTopmost(
        WindowState restoreState, bool minimize, bool hide)
    {
        using var harness = new AppHarness();
        harness.Main.WindowState = restoreState;
        if (minimize)
            harness.Main.WindowState = WindowState.Minimized;
        if (hide)
            harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        var requested = 0;
        var closed = 0;
        List<bool> topmostChanges = [];
        harness.Overlay.MainWindowRequested += () => requested++;
        harness.Overlay.Closed += (_, _) => closed++;
        harness.Main.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TopmostProperty)
                topmostChanges.Add(harness.Main.Topmost);
        };

        await ClickAsync(Named<Grid>(harness.Overlay, "OverlayHeader"), clicks: 2);

        Assert.Equal(1, requested);
        Assert.Equal(0, closed);
        Assert.True(harness.Overlay.IsVisible);
        Assert.True(harness.Main.IsVisible);
        Assert.Equal(restoreState, harness.Main.WindowState);
        Assert.False(harness.Main.Topmost);
        Assert.DoesNotContain(true, topmostChanges);
        Assert.Equal(0, harness.Runtime.ResumeCount);
        Assert.Equal(0, harness.Runtime.ListCount);
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.Space)]
    public async Task HeaderKeyboard_RequestsMainOnceWithoutInterceptingHideButton(PhysicalKey key)
    {
        using var harness = new AppHarness();
        harness.Main.WindowState = WindowState.Minimized;
        harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        await RenderAsync(harness.Overlay);
        var header = Named<Grid>(harness.Overlay, "OverlayHeader");
        var requested = 0;
        var closed = 0;
        harness.Overlay.MainWindowRequested += () => requested++;
        harness.Overlay.Closed += (_, _) => closed++;
        harness.Overlay.Activate();
        Assert.True(header.Focus());
        Assert.True(header.IsFocused);

        harness.Overlay.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        harness.Overlay.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(0, requested);
        Assert.False(harness.Main.IsVisible);
        harness.Overlay.KeyPressQwerty(key, RawInputModifiers.None);
        harness.Overlay.KeyReleaseQwerty(key, RawInputModifiers.None);
        await RenderAsync(harness.Overlay);

        Assert.Equal(1, requested);
        Assert.Equal(0, closed);
        Assert.True(harness.Overlay.IsVisible);
        Assert.True(harness.Main.IsVisible);
        Assert.Equal(WindowState.Normal, harness.Main.WindowState);
        Assert.False(harness.Main.Topmost);

        harness.Main.Hide();
        harness.Overlay.Activate();
        Assert.True(Named<Button>(harness.Overlay, "HideOverlayButton").Focus());
        harness.Overlay.KeyPressQwerty(key, RawInputModifiers.None);
        harness.Overlay.KeyReleaseQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, requested);
        Assert.Equal(0, closed);
        Assert.False(harness.Main.IsVisible);
        Assert.False(harness.Overlay.IsVisible);
        Assert.Equal(0, harness.Runtime.ResumeCount);
        Assert.Equal(0, harness.Runtime.ListCount);
    }

    [AvaloniaFact]
    public async Task SessionClick_EmitsCorrectIdAndUsesAppWorkbenchNavigation()
    {
        using var harness = new AppHarness();
        var other = harness.RegisterTask("other");
        var target = harness.RegisterTask("target", TurnPhase.Idle);
        harness.Runtime.Tasks = [other, target with { RunningJobCount = 1 }];
        harness.Main.WindowState = WindowState.Maximized;
        harness.Main.WindowState = WindowState.Minimized;
        harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        List<SessionId> requested = [];
        harness.Overlay.SessionRequested += requested.Add;
        await RenderAsync(harness.Overlay);

        await ClickAsync(SessionButton(harness.Overlay, target.SessionId));
        await ProjectionStoreTests.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return harness.Workbench.Active?.SessionId == target.SessionId
                && harness.Runtime.ListCount > 0 && harness.Workbench.GitPane is null or { IsLoading: false };
        });

        Assert.Equal(target.SessionId, Assert.Single(requested));
        Assert.Equal(target.SessionId, harness.Workbench.Active!.SessionId);
        Assert.Equal(target.Title, harness.Workbench.Active.Title);
        Assert.Equal(harness.Root, harness.Workbench.CurrentWorkspaceRoot);
        Assert.True(harness.Main.IsVisible);
        Assert.Equal(WindowState.Maximized, harness.Main.WindowState);
        Assert.False(harness.Main.Topmost);
        Assert.Equal(1, harness.Runtime.ResumeCount);
        Assert.Equal(1, harness.Runtime.Inner.SubscribeCount);
        Assert.Equal(1, harness.Runtime.Inner.ProjectionFor(target.SessionId).SnapshotCount);
        Assert.Equal(0, harness.Runtime.Inner.ProjectionFor(other.SessionId).SnapshotCount);
        Assert.Empty(harness.Runtime.Inner.Submitted);
    }

    [AvaloniaTheory]
    [InlineData(TurnOutcome.Completed, false)]
    [InlineData(TurnOutcome.Completed, true)]
    [InlineData(TurnOutcome.Failed, false)]
    [InlineData(TurnOutcome.Failed, true)]
    public async Task SessionClick_SelectedHiddenOrMinimizedTerminalRemainsUnreadUntilHitTestedRestore(
        TurnOutcome outcome, bool minimize)
    {
        using var harness = new AppHarness();
        var task = harness.RegisterTask("selected-terminal");
        harness.Runtime.Tasks = [task];
        await harness.Workbench.OpenSessionAsync(task.SessionId);
        await RenderAsync(harness.Main);
        var active = Assert.IsType<ConversationViewModel>(harness.Workbench.Active);
        var resumeCount = harness.Runtime.ResumeCount;
        harness.Main.WindowState = WindowState.Maximized;
        if (minimize)
            harness.Main.WindowState = WindowState.Minimized;
        else
            harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        await RenderAsync(harness.Overlay);
        var wasActive = harness.Main.IsActive;
        var wasVisible = harness.Main.IsVisible;
        var previousState = harness.Main.WindowState;
        var row = Assert.Single(harness.Workbench.TaskOverlay.Sessions);

        harness.FinishTask(task, outcome, "selected-hidden-result");
        await RenderAsync(harness.Overlay);

        Assert.Same(active, harness.Workbench.Active);
        Assert.Same(row, Assert.Single(harness.Workbench.TaskOverlay.Sessions));
        Assert.True(harness.Workbench.SessionActivities.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Equal(outcome == TurnOutcome.Completed, row.IsCompleted);
        Assert.Equal(outcome == TurnOutcome.Failed, row.IsFailed);
        Assert.Equal(wasActive, harness.Main.IsActive);
        Assert.Equal(wasVisible, harness.Main.IsVisible);
        Assert.Equal(previousState, harness.Main.WindowState);
        Assert.Equal(resumeCount, harness.Runtime.ResumeCount);
        AssertStatusIndicators(SessionButton(harness.Overlay, task.SessionId), busy: false,
            completed: outcome == TurnOutcome.Completed, failed: outcome == TurnOutcome.Failed);
        List<SessionId> requested = [];
        harness.Overlay.SessionRequested += requested.Add;

        await ClickAsync(SessionButton(harness.Overlay, task.SessionId));
        await RenderAsync(harness.Main);

        Assert.Equal(task.SessionId, Assert.Single(requested));
        Assert.Same(active, harness.Workbench.Active);
        Assert.True(harness.Main.IsVisible);
        Assert.True(harness.Main.IsActive);
        Assert.Equal(WindowState.Maximized, harness.Main.WindowState);
        Assert.False(harness.Main.Topmost);
        Assert.False(harness.Workbench.SessionActivities.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Empty(harness.Workbench.TaskOverlay.Sessions);
        Assert.Equal(resumeCount, harness.Runtime.ResumeCount);
        var summary = Assert.IsType<SessionSummaryViewModel>(SidebarSessionButton(harness.Main, task.SessionId).DataContext);
        Assert.False(summary.IsCompleted);
        Assert.Equal(outcome == TurnOutcome.Failed, summary.IsFailed);
    }

    [AvaloniaTheory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public async Task SessionClick_UnselectedTerminalNavigatesAndAcknowledgesOnlyItsResult(TurnOutcome outcome)
    {
        using var harness = new AppHarness();
        var current = harness.RegisterTask("current-running");
        var target = harness.RegisterTask("unselected-terminal", TurnPhase.Idle, outcome);
        harness.Runtime.Tasks = [current, target];
        await harness.Workbench.OpenSessionAsync(current.SessionId);
        harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        await RenderAsync(harness.Overlay);
        Assert.True(harness.Workbench.SessionActivities.Get(target.SessionId)!.HasUnviewedResult);
        Assert.False(harness.Main.IsVisible);

        await ClickAsync(SessionButton(harness.Overlay, target.SessionId));
        await ProjectionStoreTests.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return harness.Workbench.Active?.SessionId == target.SessionId
                && harness.Workbench.GitPane is null or { IsLoading: false };
        });
        await RenderAsync(harness.Main);

        Assert.True(harness.Main.IsVisible);
        Assert.True(harness.Main.IsActive);
        Assert.Equal(target.SessionId, harness.Workbench.Active!.SessionId);
        Assert.False(harness.Workbench.SessionActivities.Get(target.SessionId)!.HasUnviewedResult);
        Assert.Equal(current.SessionId, Assert.Single(harness.Workbench.TaskOverlay.Sessions).SessionId);
        Assert.Equal(2, harness.Runtime.ResumeCount);
        Assert.Equal(2, harness.Runtime.Inner.SubscribeCount);
        Assert.False(harness.Main.Topmost);
        Assert.Empty(harness.Runtime.Inner.Submitted);
    }

    [AvaloniaTheory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public async Task HeaderDoubleClick_SelectedHiddenTerminalAcknowledgesWithoutReopeningSession(TurnOutcome outcome)
    {
        using var harness = new AppHarness();
        var task = harness.RegisterTask("header-terminal");
        harness.Runtime.Tasks = [task];
        await harness.Workbench.OpenSessionAsync(task.SessionId);
        var active = harness.Workbench.Active;
        var resumeCount = harness.Runtime.ResumeCount;
        harness.Main.Hide();
        harness.Overlay.ShowNear(harness.Main);
        harness.FinishTask(task, outcome, "header-hidden-result");
        await RenderAsync(harness.Overlay);
        Assert.True(harness.Workbench.SessionActivities.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Single(harness.Workbench.TaskOverlay.Sessions);
        Assert.False(harness.Main.IsVisible);
        var requests = 0;
        harness.Overlay.MainWindowRequested += () => requests++;

        await ClickAsync(Named<Grid>(harness.Overlay, "OverlayHeader"), clicks: 2);
        await RenderAsync(harness.Main);

        Assert.Equal(1, requests);
        Assert.Same(active, harness.Workbench.Active);
        Assert.True(harness.Main.IsVisible);
        Assert.True(harness.Main.IsActive);
        Assert.False(harness.Workbench.SessionActivities.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Empty(harness.Workbench.TaskOverlay.Sessions);
        Assert.Equal(resumeCount, harness.Runtime.ResumeCount);
        Assert.False(harness.Main.Topmost);
    }

    [AvaloniaTheory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public async Task SidebarClick_UnseenResultDisappearsFromOverlayButFailureStaysRed(TurnOutcome outcome)
    {
        using var harness = new AppHarness(taskOverlayVisible: true);
        var current = harness.RegisterTask("sidebar-current");
        var target = harness.RegisterTask("sidebar-unseen", TurnPhase.Idle, outcome);
        harness.Runtime.Tasks = [current, target];
        await harness.Workbench.OpenSessionAsync(current.SessionId);
        harness.Main.Activate();
        await RenderAsync(harness.Main);
        await RenderAsync(harness.Overlay);
        Assert.True(harness.Main.IsActive);
        Assert.True(harness.Workbench.SessionActivities.Get(target.SessionId)!.HasUnviewedResult);
        Assert.Contains(harness.Workbench.TaskOverlay.Sessions, row => row.SessionId == target.SessionId);
        var button = SidebarSessionButton(harness.Main, target.SessionId);
        var summary = Assert.IsType<SessionSummaryViewModel>(button.DataContext);
        Assert.Equal(outcome == TurnOutcome.Completed, summary.IsCompleted);
        Assert.Equal(outcome == TurnOutcome.Failed, summary.IsFailed);

        await ClickAsync(button);
        await ProjectionStoreTests.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return harness.Workbench.Active?.SessionId == target.SessionId
                && harness.Workbench.GitPane is null or { IsLoading: false };
        });
        await RenderAsync(harness.Main);
        await RenderAsync(harness.Overlay);

        Assert.False(harness.Workbench.SessionActivities.Get(target.SessionId)!.HasUnviewedResult);
        Assert.DoesNotContain(harness.Workbench.TaskOverlay.Sessions, row => row.SessionId == target.SessionId);
        Assert.Equal(current.SessionId, Assert.Single(harness.Workbench.TaskOverlay.Sessions).SessionId);
        button = SidebarSessionButton(harness.Main, target.SessionId);
        summary = Assert.IsType<SessionSummaryViewModel>(button.DataContext);
        Assert.True(summary.IsActive);
        Assert.False(summary.IsCompleted);
        Assert.Equal(outcome == TurnOutcome.Failed, summary.IsFailed);
        Assert.False(Named<Border>(button, "PART_CompletedIndicator").IsEffectivelyVisible);
        var failed = Named<Border>(button, "PART_FailedIndicator");
        Assert.Equal(outcome == TurnOutcome.Failed, failed.IsEffectivelyVisible);
        if (outcome == TurnOutcome.Failed)
            AssertThemeColor(harness.Main, failed.Background, "AlcolerError");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundActivity_UpdatesHiddenOverlayWithoutRestoringOrActivatingMain(bool minimize)
    {
        using var harness = new AppHarness();
        var task = harness.RegisterTask("background-hidden");
        if (minimize)
            harness.Main.WindowState = WindowState.Minimized;
        else
            harness.Main.Hide();
        Dispatcher.UIThread.RunJobs();
        var wasVisible = harness.Main.IsVisible;
        var wasActive = harness.Main.IsActive;
        var previousState = harness.Main.WindowState;
        var queries = harness.Runtime.TaskQueryCount;
        Assert.False(harness.Overlay.IsVisible);
        Assert.False(RefreshTimer(harness.Overlay).IsEnabled);

        harness.Runtime.Tasks = [task];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(task.SessionId, Assert.Single(harness.Workbench.TaskOverlay.Sessions).SessionId);
        Assert.True(harness.Runtime.TaskQueryCount > queries);
        harness.FinishTask(task, TurnOutcome.Completed, "background-hidden-finish");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.Single(harness.Workbench.TaskOverlay.Sessions).IsCompleted);
        Assert.True(harness.Workbench.SessionActivities.Get(task.SessionId)!.HasUnviewedResult);
        Assert.False(harness.Overlay.IsVisible);
        Assert.False(RefreshTimer(harness.Overlay).IsEnabled);
        Assert.Equal(wasVisible, harness.Main.IsVisible);
        Assert.Equal(wasActive, harness.Main.IsActive);
        Assert.Equal(previousState, harness.Main.WindowState);
        Assert.Null(harness.Workbench.Active);
        Assert.Equal(0, harness.Runtime.ResumeCount);
    }

    [AvaloniaFact]
    public async Task CloseAndHideButton_KeepWindowAliveAndAllowReopeningWithoutMainRequest()
    {
        using var harness = new AppHarness();
        var task = harness.RegisterTask("keep-alive");
        harness.Runtime.Tasks = [task];
        var closed = 0;
        var mainRequests = 0;
        harness.Overlay.Closed += (_, _) => closed++;
        harness.Overlay.MainWindowRequested += () => mainRequests++;
        harness.Overlay.ShowNear(harness.Main);
        await RenderAsync(harness.Overlay);
        var row = Assert.Single(harness.Workbench.TaskOverlay.Sessions);
        var position = harness.Overlay.Position;

        harness.Overlay.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.Overlay.IsVisible);
        Assert.Equal(0, closed);
        Assert.False(RefreshTimer(harness.Overlay).IsEnabled);
        harness.Overlay.ShowNear(harness.Main);
        await RenderAsync(harness.Overlay);
        Assert.True(harness.Overlay.IsVisible);
        Assert.Equal(position, harness.Overlay.Position);
        Assert.Same(row, Assert.Single(harness.Workbench.TaskOverlay.Sessions));

        await ClickAsync(Named<Button>(harness.Overlay, "HideOverlayButton"));

        Assert.False(harness.Overlay.IsVisible);
        Assert.Equal(0, mainRequests);
        Assert.Equal(0, closed);
        Assert.False(RefreshTimer(harness.Overlay).IsEnabled);
        harness.Overlay.ShowNear(harness.Main);
        Assert.True(harness.Overlay.IsVisible);
        Assert.True(RefreshTimer(harness.Overlay).IsEnabled);
    }

    [AvaloniaFact]
    public async Task RefreshTimer_TicksDurationOnlyWhileVisibleWithoutQueryingRuntimeAndHiddenStateStillUpdates()
    {
        var task = TaskOverlayViewModelTests.TaskSnapshot("visible", startedAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        var runtime = new TaskOverlayActivityRuntime { Tasks = [task] };
        var tracker = TaskOverlayViewModelTests.CreateTracker(runtime.GetSessionActivities().ToArray());
        var model = new TaskOverlayViewModel(tracker);
        var window = new TaskOverlayWindow(model);
        var timer = RefreshTimer(window);
        var closed = 0;
        var queries = runtime.TaskQueryCount;
        window.Closed += (_, _) => closed++;
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(1), timer.Interval);
            Assert.False(timer.IsEnabled);
            window.Show();
            await RenderAsync(window);
            Assert.True(timer.IsEnabled);
            var row = Assert.Single(model.Sessions);
            tracker.ApplyTitle(task.SessionId, "Shared state update");
            Assert.Equal("Shared state update", row.Title);
            var duration = row.DurationText;
            await ProjectionStoreTests.WaitUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return row.DurationText != duration;
            });
            Assert.Equal(queries, runtime.TaskQueryCount);

            window.Hide();
            Assert.False(timer.IsEnabled);
            duration = row.DurationText;
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(duration, row.DurationText);
            Assert.Equal(queries, runtime.TaskQueryCount);

            tracker.ApplyActivity(task.SessionId, task with
            {
                Phase = TurnPhase.Idle,
                LastOutcome = TurnOutcome.Completed,
                HasActiveWork = false,
                ActiveWorkStartedAt = null,
                CompletedTurnId = new TurnId("hidden-timer-finish"),
                FinishedAt = task.StartedAt!.Value.AddSeconds(3),
            });

            Assert.False(window.IsVisible);
            Assert.Same(row, Assert.Single(model.Sessions));
            Assert.True(row.IsCompleted);
            Assert.False(row.IsBusy);
            Assert.Equal(Loc.Get("duration_seconds", 3), row.DurationText);
            Assert.Equal(queries, runtime.TaskQueryCount);
            tracker.SetViewingSession(task.SessionId);
            Assert.Empty(model.Sessions);
            window.Show();
            Assert.True(timer.IsEnabled);
            Assert.Empty(model.Sessions);
            Assert.Equal(queries, runtime.TaskQueryCount);
            window.Shutdown();
            Assert.Equal(1, closed);
            Assert.False(window.IsVisible);
            Assert.False(timer.IsEnabled);
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(queries, runtime.TaskQueryCount);
            Assert.Equal(0, runtime.ResumeCount);
            Assert.Equal(0, runtime.ListCount);
        }
        finally
        {
            if (closed == 0)
                window.Shutdown();
        }
    }

    [AvaloniaFact]
    public async Task Surface_UsesLightAndDarkTranslucentThemeBrushWithTransparentWindow()
    {
        var window = new TaskOverlayWindow(new TaskOverlayViewModel(new SessionActivityTracker()));
        window.Show();
        try
        {
            List<Color> colors = [];
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await RenderAsync(window);
                var surface = Named<Border>(window, "OverlaySurface");
                var brush = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background);
                Assert.True(window.TryFindResource("AlcolerTaskOverlay", theme, out var resource));
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color, brush.Color);
                Assert.Equal(theme, surface.ActualThemeVariant);
                Assert.InRange(brush.Color.A, (byte)1, (byte)254);
                Assert.Equal((byte)0, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color.A);
                Assert.Contains(WindowTransparencyLevel.Transparent, window.TransparencyLevelHint);
                colors.Add(brush.Color);
            }
            Assert.NotEqual(colors[0], colors[1]);
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
            Assert.False(window.ShowActivated);
            Assert.Equal(SystemDecorations.None, window.SystemDecorations);
        }
        finally
        {
            window.Shutdown();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppInitialization_UsesSharedInitialOverlayVisibility(bool visible)
    {
        using var harness = new AppHarness(taskOverlayVisible: visible);

        AssertOverlayVisibility(harness, visible);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh")]
    public async Task SettingsAppearance_MouseToggleImmediatelyControlsOverlayTimerAndTray(string language)
    {
        var previousLanguage = Loc.Language;
        Loc.SetLanguage(language);
        try
        {
            using var harness = new AppHarness();
            var dialog = await OpenAppearanceSettingsAsync(harness);
            var settings = Assert.IsType<SettingsViewModel>(dialog.DataContext);
            var card = Named<Border>(dialog, "TaskOverlaySetting");
            var toggle = Named<ToggleSwitch>(dialog, "TaskOverlayToggle");
            Assert.Same(harness.Workbench.TaskOverlay, settings.TaskOverlay);
            Assert.True(settings.CanToggleTaskOverlay);
            Assert.True(card.IsEffectivelyVisible);
            Assert.Contains(card.GetVisualDescendants(), control => ReferenceEquals(control, toggle));
            foreach (var key in new[] { "settings_task_overlay", "settings_task_overlay_description" })
            {
                var localized = Loc.Get(key);
                Assert.NotEqual(key, localized);
                Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == localized);
            }
            AssertOverlayVisibility(harness, false, toggle);

            await ClickAsync(toggle);

            AssertOverlayVisibility(harness, true, toggle);
            Assert.True(dialog.IsVisible);
            await ClickAsync(toggle);
            AssertOverlayVisibility(harness, false, toggle);
            Assert.True(dialog.IsVisible);
            Assert.Null(harness.Workbench.Active);
            Assert.Equal(0, harness.Runtime.ResumeCount);
            Assert.Empty(harness.Runtime.Inner.Submitted);
        }
        finally
        {
            Loc.SetLanguage(previousLanguage);
        }
    }

    [AvaloniaTheory]
    [InlineData("CancelButton")]
    [InlineData("CloseButton")]
    [InlineData("Escape")]
    public async Task SettingsDismissal_DoesNotRollBackLiveVisibilityAndReopeningReadsCurrentState(string dismissal)
    {
        using var harness = new AppHarness();
        foreach (var visible in new[] { true, false })
        {
            var dialog = await OpenAppearanceSettingsAsync(harness);
            var toggle = Named<ToggleSwitch>(dialog, "TaskOverlayToggle");
            Assert.Same(harness.Workbench.TaskOverlay, Assert.IsType<SettingsViewModel>(dialog.DataContext).TaskOverlay);
            AssertOverlayVisibility(harness, !visible, toggle);
            await ClickAsync(toggle);
            AssertOverlayVisibility(harness, visible, toggle);

            if (dismissal == "Escape")
            {
                dialog.Activate();
                Assert.True(toggle.Focus());
                dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                harness.Main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }
            else
            {
                await ClickAsync(Named<Button>(dialog, dismissal));
            }

            Assert.False(dialog.IsVisible);
            Assert.Empty(harness.Main.OwnedWindows);
            AssertOverlayVisibility(harness, visible);
        }

        var reopened = await OpenAppearanceSettingsAsync(harness);
        AssertOverlayVisibility(harness, false, Named<ToggleSwitch>(reopened, "TaskOverlayToggle"));
        Assert.False(new TaskOverlayViewModel(harness.Workbench.SessionActivities).IsVisible);
        Assert.Equal(0, harness.Runtime.ResumeCount);
        Assert.Empty(harness.Runtime.Inner.Submitted);
    }

    [AvaloniaFact]
    public async Task SettingsAppearance_WithoutLiveModelHidesOverlayCard()
    {
        using var harness = new AppHarness();
        var result = SettingsDialog.ShowAsync(harness.Main, new WorkbenchConfig([]));
        var dialog = Assert.IsType<SettingsDialog>(Assert.Single(harness.Main.OwnedWindows));
        await ClickAsync(Named<RadioButton>(dialog, "AppearanceTab"));
        var settings = Assert.IsType<SettingsViewModel>(dialog.DataContext);

        Assert.True(Named<ScrollViewer>(dialog, "AppearancePanel").IsEffectivelyVisible);
        Assert.Null(settings.TaskOverlay);
        Assert.False(settings.CanToggleTaskOverlay);
        Assert.False(Named<Border>(dialog, "TaskOverlaySetting").IsVisible);
        Assert.False(Named<ToggleSwitch>(dialog, "TaskOverlayToggle").IsEffectivelyVisible);
        AssertOverlayVisibility(harness, false);
        await ClickAsync(Named<Button>(dialog, "CancelButton"));
        Assert.Null(await result);
    }

    [AvaloniaFact]
    public async Task AppTrayCloseAndHide_SynchronizeSettingsWithoutNavigationOrStoppingWork()
    {
        using var harness = new AppHarness();
        var task = harness.RegisterTask("keep-running") with { RunningJobCount = 1, RunningSubagentCount = 1 };
        harness.Runtime.Tasks = [task];
        await harness.Workbench.OpenSessionAsync(task.SessionId);
        await ProjectionStoreTests.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return harness.Workbench.GitPane is null or { IsLoading: false };
        });
        var active = Assert.IsType<ConversationViewModel>(harness.Workbench.Active);
        active.Composer.Draft = "Preserve the active task draft";
        var dialog = await OpenAppearanceSettingsAsync(harness);
        var settingsToggle = Named<ToggleSwitch>(dialog, "TaskOverlayToggle");
        var tray = PrivateField<TrayIcon>(harness.App, "_trayIcon");
        var menu = Assert.IsType<NativeMenu>(tray.Menu);
        var toggle = TrayOverlayToggle(harness.App);
        var resumeCount = harness.Runtime.ResumeCount;
        var listCount = harness.Runtime.ListCount;
        var subscribeCount = harness.Runtime.Inner.SubscribeCount;
        var mainRequests = 0;
        var sessionRequests = 0;
        var closed = 0;
        harness.Overlay.MainWindowRequested += () => mainRequests++;
        harness.Overlay.SessionRequested += _ => sessionRequests++;
        harness.Overlay.Closed += (_, _) => closed++;
        Assert.Equal(NativeMenuItemToggleType.CheckBox, toggle.ToggleType);
        AssertOverlayVisibility(harness, false, settingsToggle);

        NativeClick(toggle);
        AssertOverlayVisibility(harness, true, settingsToggle);
        var row = Assert.Single(harness.Workbench.TaskOverlay.Sessions);
        Assert.Equal(task.SessionId, row.SessionId);
        NativeClick(toggle);
        AssertOverlayVisibility(harness, false, settingsToggle);
        NativeClick(toggle);
        harness.Overlay.Close();
        AssertOverlayVisibility(harness, false, settingsToggle);
        NativeClick(toggle);
        await ClickAsync(Named<Button>(harness.Overlay, "HideOverlayButton"));
        AssertOverlayVisibility(harness, false, settingsToggle);
        NativeClick(toggle);
        harness.Overlay.Hide();
        AssertOverlayVisibility(harness, false, settingsToggle);

        Assert.True(dialog.IsVisible);
        Assert.Same(row, Assert.Single(harness.Workbench.TaskOverlay.Sessions));
        Assert.Same(active, harness.Workbench.Active);
        Assert.Equal("Preserve the active task draft", active.Composer.Draft);
        Assert.Equal(resumeCount, harness.Runtime.ResumeCount);
        Assert.Equal(listCount, harness.Runtime.ListCount);
        Assert.Equal(subscribeCount, harness.Runtime.Inner.SubscribeCount);
        Assert.Empty(harness.Runtime.Inner.Submitted);
        Assert.Equal(0, mainRequests);
        Assert.Equal(0, sessionRequests);
        Assert.Equal(0, closed);
        await ClickAsync(Named<Button>(dialog, "CancelButton"));
        harness.Main.Hide();
        NativeClick(Assert.Single(menu.Items.OfType<NativeMenuItem>(), item => Equals(item.Header, Loc.Get("tray_open"))));
        Assert.True(harness.Main.IsVisible);
        Assert.False(harness.Main.Topmost);
    }

    [AvaloniaFact]
    public void AppDesktopExit_ClosesOverlayStopsTimerAndPreventsModelChangesFromReopeningIt()
    {
        using var harness = new AppHarness();
        harness.Workbench.TaskOverlay.IsVisible = true;
        var timer = RefreshTimer(harness.Overlay);
        var closed = 0;
        harness.Overlay.Closed += (_, _) => closed++;
        AssertOverlayVisibility(harness, true);

        harness.Lifetime.Shutdown();

        Assert.Equal(1, closed);
        Assert.False(harness.Overlay.IsVisible);
        Assert.False(timer.IsEnabled);
        Assert.Null(typeof(App).GetField("_taskOverlayWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.App));
        Assert.Null(typeof(App).GetField("_trayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.App));
        var queries = harness.Runtime.TaskQueryCount;

        harness.Workbench.TaskOverlay.IsVisible = false;
        harness.Workbench.TaskOverlay.IsVisible = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, closed);
        Assert.False(harness.Overlay.IsVisible);
        Assert.False(timer.IsEnabled);
        Assert.Equal(queries, harness.Runtime.TaskQueryCount);
        Assert.Null(typeof(App).GetField("_taskOverlayWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.App));
    }

    private static async Task<SettingsDialog> OpenAppearanceSettingsAsync(AppHarness harness)
    {
        await RenderAsync(harness.Main);
        await ClickAsync(Named<Button>(harness.Main, "PART_Settings"));
        var dialog = Assert.IsType<SettingsDialog>(Assert.Single(harness.Main.OwnedWindows));
        await ClickAsync(Named<RadioButton>(dialog, "AppearanceTab"));
        Assert.True(Named<ScrollViewer>(dialog, "AppearancePanel").IsEffectivelyVisible);
        return dialog;
    }

    private static void AssertOverlayVisibility(AppHarness harness, bool expected, ToggleSwitch? settingsToggle = null)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(expected, harness.Workbench.TaskOverlay.IsVisible);
        Assert.Equal(expected, harness.Overlay.IsVisible);
        Assert.Equal(expected, RefreshTimer(harness.Overlay).IsEnabled);
        Assert.Equal(expected, TrayOverlayToggle(harness.App).IsChecked);
        if (settingsToggle is not null)
            Assert.Equal((bool?)expected, settingsToggle.IsChecked);
    }

    private static NativeMenuItem TrayOverlayToggle(App app) =>
        Assert.Single(Assert.IsType<NativeMenu>(PrivateField<TrayIcon>(app, "_trayIcon").Menu)
            .Items.OfType<NativeMenuItem>(), item => Equals(item.Header, Loc.Get("tray_task_overlay")));

    private static void AssertStatusIndicators(Button button, bool busy, bool completed, bool failed)
    {
        var spinner = Named<Icon>(button, "PART_RunningIndicator");
        var completedDot = Named<Border>(button, "PART_CompletedIndicator");
        var failedDot = Named<Border>(button, "PART_FailedIndicator");
        Assert.Equal(busy, spinner.IsVisible);
        Assert.Equal(busy, spinner.IsEffectivelyVisible);
        Assert.Equal(busy, spinner.Classes.Contains("running"));
        Assert.Equal(completed, completedDot.IsVisible);
        Assert.Equal(completed, completedDot.IsEffectivelyVisible);
        Assert.Equal(failed, failedDot.IsVisible);
        Assert.Equal(failed, failedDot.IsEffectivelyVisible);
        Assert.Equal(Loc.Get("sidebar_running"), ToolTip.GetTip(spinner));
        Assert.Equal(Loc.Get("sidebar_running"), AutomationProperties.GetName(spinner));
        Assert.Equal(Loc.Get("sidebar_completed"), ToolTip.GetTip(completedDot));
        Assert.Equal(Loc.Get("sidebar_completed"), AutomationProperties.GetName(completedDot));
        Assert.Equal(Loc.Get("sidebar_failed"), ToolTip.GetTip(failedDot));
        Assert.Equal(Loc.Get("sidebar_failed"), AutomationProperties.GetName(failedDot));
    }

    private static Color AssertThemeColor(TopLevel root, IBrush? brush, string key)
    {
        Assert.True(root.TryFindResource(key, root.ActualThemeVariant, out var resource));
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color, color);
        return color;
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Button SessionButton(TaskOverlayWindow window, SessionId sessionId) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("session")
            && button.DataContext is TaskOverlaySessionViewModel row && row.SessionId == sessionId);

    private static Button SidebarSessionButton(MainWindow window, SessionId sessionId) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("sessionItem")
            && button.DataContext is SessionSummaryViewModel row && row.Id == sessionId);

    private static DispatcherTimer RefreshTimer(TaskOverlayWindow window) =>
        Assert.IsType<DispatcherTimer>(Assert.Single(typeof(TaskOverlayWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => field.FieldType == typeof(DispatcherTimer)).GetValue(window));

    private static T PrivateField<T>(object instance, string name) =>
        Assert.IsType<T>(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));

    private static void NativeClick(NativeMenuItem item)
    {
        var bridge = Assert.Single(typeof(NativeMenuItem).GetInterfaces(),
            type => type.Name == "INativeMenuItemExporterEventsImplBridge");
        var click = Assert.Single(bridge.GetMethods(), method => method.Name == "RaiseClicked");
        click.Invoke(item, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task ClickAsync(Control control, int clicks = 1)
    {
        var root = TopLevel.GetTopLevel(control);
        Assert.NotNull(root);
        await RenderAsync(root);
        control.BringIntoView();
        await RenderAsync(root);
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Assert.True(new Rect(root.ClientSize).Contains(point),
            $"Expected {control.GetType().Name} {control.Name} at {point} inside {root.ClientSize}.");
        root.MouseMove(point);
        await RenderAsync(root);
        var hit = Assert.IsAssignableFrom<Visual>(root.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control),
            $"Expected {control.GetType().Name} {control.Name} at {point}, hit {hit.GetType().Name}.");
        for (var index = 0; index < clicks; index++)
        {
            root.MouseDown(point, MouseButton.Left);
            root.MouseUp(point, MouseButton.Left);
        }
        if (root.IsVisible)
            await RenderAsync(root);
        else
            Dispatcher.UIThread.RunJobs();
    }

    private static async Task RenderAsync(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(root);
        Assert.NotNull(visual);
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }

    private sealed class AppHarness : IDisposable
    {
        public AppHarness(bool taskOverlayVisible = false)
        {
            Workbench = new WorkbenchViewModel(Runtime, new AvaloniaUiScheduler(), new WorkbenchConfig([]));
            Assert.False(Workbench.TaskOverlay.IsVisible);
            Workbench.TaskOverlay.IsVisible = taskOverlayVisible;
            App.InitializeWorkbench(Lifetime, Workbench);
            Main = Assert.IsType<MainWindow>(Lifetime.MainWindow);
            Overlay = PrivateField<TaskOverlayWindow>(App, "_taskOverlayWindow");
            Assert.Same(Workbench.TaskOverlay, Overlay.DataContext);
            Main.Show();
            Main.Activate();
            Dispatcher.UIThread.RunJobs();
        }

        public string Root { get; } = Directory.CreateTempSubdirectory("alcoler-task-overlay-").FullName;
        public TaskOverlayActivityRuntime Runtime { get; } = new();
        public App App { get; } = new();
        public FakeDesktopLifetime Lifetime { get; } = new();
        public WorkbenchViewModel Workbench { get; }
        public MainWindow Main { get; }
        public TaskOverlayWindow Overlay { get; }

        public SessionActivitySnapshot RegisterTask(string id, TurnPhase phase = TurnPhase.Streaming,
            TurnOutcome? outcome = null)
        {
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var task = TaskOverlayViewModelTests.TaskSnapshot(id, phase, startedAt) with
            {
                WorkspaceRoot = Root,
                LastOutcome = outcome,
                LastActivityUtc = startedAt.AddSeconds(55),
                CompletedTurnId = outcome is null ? null : new TurnId($"{id}-initial-finish"),
                FinishedAt = outcome is null ? null : startedAt.AddSeconds(55),
                ActiveWorkStartedAt = phase is TurnPhase.Idle or TurnPhase.Error ? null : startedAt,
            };
            Runtime.Inner.RegisterSession(task.SessionId,
                new ConversationSnapshot(0, 1, [], TestSnapshots.State(phase) with { Title = task.Title }), Root);
            Runtime.SessionSummaries = [.. Runtime.SessionSummaries,
                new SessionSummary(task.SessionId, task.Title, Root, PermissionMode.Build, null, startedAt, task.LastActivityUtc)];
            return task;
        }

        public SessionActivitySnapshot FinishTask(SessionActivitySnapshot task, TurnOutcome outcome, string turnId)
        {
            var finished = task with
            {
                Phase = TurnPhase.Idle,
                LastOutcome = outcome,
                HasActiveWork = false,
                IsAwaitingConfirmation = false,
                RunningJobCount = 0,
                RunningSubagentCount = 0,
                ActiveWorkStartedAt = null,
                CompletedTurnId = new TurnId(turnId),
                FinishedAt = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow,
            };
            var state = TestSnapshots.State(TurnPhase.Idle) with { Title = task.Title };
            var projection = Runtime.Inner.ProjectionFor(task.SessionId);
            projection.SetSnapshot(new ConversationSnapshot(0, 2, [], state));
            projection.Emit(new ConversationDelta(2, [new StatePatched(state)]));
            Runtime.Tasks = Runtime.Tasks.Select(existing => existing.SessionId == task.SessionId ? finished : existing).ToArray();
            return finished;
        }

        public void Dispose()
        {
            foreach (var dialog in Main.OwnedWindows.ToArray())
                dialog.Close();
            Lifetime.Shutdown();
            typeof(App).GetField("_isExiting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(App, true);
            Main.Close();
            Workbench.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeDesktopLifetime : IClassicDesktopStyleApplicationLifetime
    {
        private bool _exited;

        public string[] Args => [];
        public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnExplicitShutdown;
        public Window? MainWindow { get; set; }
        public IReadOnlyList<Window> Windows => MainWindow is { } main ? [main] : [];
        public event EventHandler<ControlledApplicationLifetimeStartupEventArgs>? Startup { add { } remove { } }
        public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested { add { } remove { } }
        public event EventHandler<ControlledApplicationLifetimeExitEventArgs>? Exit;

        public void Shutdown(int exitCode = 0)
        {
            if (_exited)
                return;
            _exited = true;
            Exit?.Invoke(this, new ControlledApplicationLifetimeExitEventArgs(exitCode));
        }

        public bool TryShutdown(int exitCode = 0)
        {
            Shutdown(exitCode);
            return true;
        }
    }
}
