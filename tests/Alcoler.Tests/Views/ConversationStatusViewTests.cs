using System.Reflection;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Git;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
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

public sealed class ConversationStatusViewTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [AvaloniaFact]
    public async Task StartedSession_KeepsCardVisible_WhenLastStatusContentIsRemoved()
    {
        using var harness = new Harness(1400,
            new UserMessageRow("user", "Not status content", StartedAt),
            new SystemNoticeRow("notice", "notice", "Not a typed activity"));
        await RenderAsync(harness.Window);

        Assert.True(harness.View.IsVisible);
        Assert.False(harness.View.IsExpanded);
        Assert.Null(harness.View.ExpandedOverride);

        Apply(harness.Model, new RowAppended(Todo("todos", "Inspect status")));
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.True(harness.View.IsExpanded);

        await ClickAsync(Named<Button>(harness.View, "MinimizeButton"));
        await ClickAsync(Named<Button>(harness.View, "SummaryButton"));
        Assert.Equal(true, harness.View.ExpandedOverride);

        Apply(harness.Model, new RowRemoved("todos"));
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.True(harness.View.IsExpanded);
        Assert.Equal(true, harness.View.ExpandedOverride);

        Apply(harness.Model, new RowAppended(Todo("replacement", "Status returns")));
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.True(harness.View.IsExpanded);
    }

    [AvaloniaFact]
    public async Task ManualExpandAndMinimize_SurviveProjectionUpdatesResetsAndConversationResizes()
    {
        using var harness = new Harness(900, Todo("todos", "Inspect status"));
        await RenderAsync(harness.Window);
        var modeChanges = 0;
        harness.View.DisplayModeChanged += (_, _) => modeChanges++;

        await ClickAsync(Named<Button>(harness.View, "SummaryButton"));
        Assert.True(harness.View.IsExpanded);
        Assert.Equal(true, harness.View.ExpandedOverride);
        Assert.Equal(1, modeChanges);

        Apply(harness.Model, new RowUpserted(Todo("todos", "Projection changed")));
        await harness.ResizeConversationAsync(1400);
        Reset(harness.Model, Todo("reset", "Resubscribed snapshot"));
        await harness.ResizeConversationAsync(700);
        Assert.True(harness.View.IsExpanded);
        Assert.Equal(true, harness.View.ExpandedOverride);
        Assert.Equal(1, modeChanges);

        await ClickAsync(Named<Button>(harness.View, "MinimizeButton"));
        Assert.False(harness.View.IsExpanded);
        Assert.Equal(false, harness.View.ExpandedOverride);
        Assert.Equal(2, modeChanges);

        Apply(harness.Model, new RowUpserted(Todo("reset", "Another update")));
        await harness.ResizeConversationAsync(1400);
        Reset(harness.Model, Todo("reset-again", "Another snapshot"));
        await RenderAsync(harness.Window);
        Assert.False(harness.View.IsExpanded);
        Assert.Equal(false, harness.View.ExpandedOverride);
        Assert.Equal(2, modeChanges);
    }

    [AvaloniaFact]
    public async Task DisplayModeMenu_ContainsOnlyAutoExpand_AndClickRestoresResponsiveMode()
    {
        using var harness = new Harness(900, Todo("todos", "Inspect status"));
        await ClickAsync(Named<Button>(harness.View, "SummaryButton"));
        var displayMode = Named<Button>(harness.View, "DisplayModeButton");
        var menu = Assert.IsType<MenuFlyout>(displayMode.Flyout);
        var autoExpand = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal("Auto expand", autoExpand.Header);

        await ClickAsync(displayMode);
        Assert.True(menu.IsOpen);
        await ClickAsync(autoExpand);
        Assert.False(menu.IsOpen);
        Assert.Null(harness.View.ExpandedOverride);
        Assert.False(harness.View.IsExpanded);

        await harness.ResizeConversationAsync(1280);
        Assert.True(harness.View.IsExpanded);
        await harness.ResizeConversationAsync(1279);
        Assert.False(harness.View.IsExpanded);
    }

    [AvaloniaFact]
    public async Task Sections_DefaultToGitPlansProgressOpenAndActivitiesClosed_AndPreserveUserChoices()
    {
        using var harness = new Harness(1400, FullRows());
        harness.Model.BindGit(CreateGit());
        await RenderAsync(harness.Window);
        AssertSections(harness.View, git: true, plans: true, progress: true, terminals: false, agents: false);
        foreach (var name in new[] { "GitContainer", "PlansContainer", "ProgressContainer", "TerminalsContainer", "AgentsContainer" })
        {
            Assert.True(Named<Border>(harness.View, name).IsEffectivelyVisible);
        }

        var gitContainer = Named<Border>(harness.View, "GitContainer");
        var changes = Named<Button>(harness.View, "ChangesButton");
        Assert.InRange(changes.Bounds.Width, gitContainer.Bounds.Width - 1, gitContainer.Bounds.Width);

        foreach (var name in new[] { "GitSection", "PlansSection", "ProgressSection", "TerminalsSection", "AgentsSection" })
        {
            await ToggleSectionAsync(harness.View, name);
        }
        AssertSections(harness.View, git: false, plans: false, progress: false, terminals: true, agents: true);

        Apply(harness.Model, new RowUpserted(Todo("todos", "Updated task")),
            new RowUpserted(Plan("plan", "Updated plan")),
            new RowUpserted(Terminal("terminal", "Updated terminal")),
            new RowUpserted(Agent("agent", "Updated agent")));
        await harness.ResizeConversationAsync(900);
        await harness.ResizeConversationAsync(1400);
        AssertSections(harness.View, git: false, plans: false, progress: false, terminals: true, agents: true);

        Apply(harness.Model, new RowRemoved("terminal"), new RowRemoved("agent"));
        await RenderAsync(harness.Window);
        Assert.False(Named<Expander>(harness.View, "TerminalsSection").IsExpanded);
        Assert.False(Named<Expander>(harness.View, "AgentsSection").IsExpanded);
        Assert.False(Named<Border>(harness.View, "TerminalsContainer").IsVisible);
        Assert.False(Named<Border>(harness.View, "AgentsContainer").IsVisible);

        Apply(harness.Model, new RowAppended(Terminal("terminal-again", "New terminal")),
            new RowAppended(Agent("agent-again", "New agent")));
        await RenderAsync(harness.Window);
        AssertSections(harness.View, git: false, plans: false, progress: false, terminals: false, agents: false);
    }

    [AvaloniaFact]
    public async Task ContextChange_ResetsSectionDefaults_AndPreservesManualDisplayOverride()
    {
        using var harness = new Harness(1400, FullRows());
        harness.Model.BindGit(CreateGit());
        await ToggleSectionAsync(harness.View, "GitSection");
        await ToggleSectionAsync(harness.View, "PlansSection");
        await ToggleSectionAsync(harness.View, "ProgressSection");
        await ToggleSectionAsync(harness.View, "TerminalsSection");
        await ToggleSectionAsync(harness.View, "AgentsSection");
        await ClickAsync(Named<Button>(harness.View, "MinimizeButton"));
        Assert.Equal(false, harness.View.ExpandedOverride);
        var previous = harness.Model;

        var replacement = harness.CreateModel(FullRows());
        replacement.BindGit(CreateGit());
        harness.View.DataContext = replacement;
        await RenderAsync(harness.Window);
        Assert.Equal(false, harness.View.ExpandedOverride);
        Assert.False(harness.View.IsExpanded);
        AssertSections(harness.View, git: true, plans: true, progress: true, terminals: false, agents: false);

        await harness.ResizeConversationAsync(900);
        await ClickAsync(Named<Button>(harness.View, "SummaryButton"));
        Assert.Equal(true, harness.View.ExpandedOverride);
        harness.View.DataContext = harness.CreateModel(Todo("new-session", "New conversation"));
        await RenderAsync(harness.Window);
        Assert.Equal(true, harness.View.ExpandedOverride);
        Assert.True(harness.View.IsExpanded);
        Assert.Equal("New conversation", harness.Model.SummaryText);

        Reset(previous);
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.True(harness.View.IsExpanded);
    }

    [AvaloniaFact]
    public async Task ReviewAndPlanRows_ClickThroughToTheirNavigationCallbacks()
    {
        using var harness = new Harness(1400, Plan("plan", "Implementation plan"));
        harness.Model.BindGit(CreateGit());
        var reviews = 0;
        List<StatusPlan> requestedPlans = [];
        harness.View.ReviewRequested += () => reviews++;
        harness.View.PlanRequested += requestedPlans.Add;
        await RenderAsync(harness.Window);

        await ClickAsync(Named<Button>(harness.View, "ChangesButton"));
        Assert.Equal(1, reviews);
        Assert.Empty(requestedPlans);
        var plan = Assert.Single(harness.Model.Plans);
        var planButton = Assert.Single(harness.View.GetVisualDescendants().OfType<Button>(),
            button => ReferenceEquals(button.DataContext, plan));
        await ClickAsync(planButton);

        Assert.Same(plan, Assert.Single(requestedPlans));
        Assert.Equal("plan", requestedPlans[0].RowId);
        Assert.Equal("# Implementation plan\n\nInspect and implement.", requestedPlans[0].Markdown);
        Assert.Equal(1, reviews);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task FourAgents_RenderDistinctRows_AndMoveCompletedChildrenIntoEndedDirectory()
    {
        var agents = Enumerable.Range(1, 4).Select(index => Agent($"agent-{index}", "Independent review")).ToArray();
        using var harness = new Harness(1400, agents);
        await ToggleSectionAsync(harness.View, "AgentsSection");
        Assert.Equal("4 running", harness.Model.AgentCountText);
        Assert.Equal("4 running", Named<TextBlock>(harness.View, "AgentRunningCount").Text);
        foreach (var agent in agents)
            Assert.True(ActivityButton(harness.View, agent.RowId).IsEffectivelyVisible);

        Apply(harness.Model, agents.Take(3).Select(agent => (RowOp)new RowUpserted(agent with
        {
            Subagent = agent.Subagent! with { Status = SubagentStatus.Completed, EndedAt = StartedAt.AddMinutes(1) },
        })).ToArray());
        await RenderAsync(harness.Window);
        Assert.Equal("1 running", harness.Model.AgentCountText);
        var runningCount = Named<TextBlock>(harness.View, "AgentRunningCount");
        Assert.True(runningCount.IsEffectivelyVisible);
        Assert.Equal("1 running", runningCount.Text);
        Assert.Equal("agent-4", Assert.Single(harness.Model.Agents).RowId);
        Assert.True(ActivityButton(harness.View, "agent-4").IsEffectivelyVisible);
        Assert.Single(harness.View.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("activityRow") && button.IsEffectivelyVisible);
        var endedButton = Named<Button>(harness.View, "EndedAgentsButton");
        await ClickAsync(endedButton);
        var directory = Assert.IsAssignableFrom<Control>(Assert.IsType<Flyout>(endedButton.Flyout).Content);
        foreach (var agent in agents.Take(3))
            Assert.True(ActivityButton(directory, agent.RowId).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Agents_LastRunningCompletionClosesGroupButKeepsEndedEntryAccessible()
    {
        var running = Agent("running", "Inspect production view");
        using var harness = new Harness(1400, running, Agent("ended", "Earlier review", SubagentStatus.Completed));
        await RenderAsync(harness.Window);
        var section = Named<Expander>(harness.View, "AgentsSection");
        var count = Named<TextBlock>(harness.View, "AgentRunningCount");
        Assert.False(section.IsExpanded);
        Assert.True(count.IsEffectivelyVisible);
        Assert.Equal("1 running", count.Text);
        await ToggleSectionAsync(harness.View, "AgentsSection");

        Apply(harness.Model, new RowUpserted(running with
        {
            Subagent = running.Subagent! with { Status = SubagentStatus.Completed, EndedAt = StartedAt.AddMinutes(2) },
        }));
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.True(Named<Border>(harness.View, "AgentsContainer").IsEffectivelyVisible);
        Assert.False(section.IsExpanded);
        Assert.False(count.IsVisible);
        Assert.Equal(string.Empty, count.Text);
        Assert.Equal("2 ended agents", harness.Model.SummaryText);

        await ToggleSectionAsync(harness.View, "AgentsSection");
        var endedButton = Named<Button>(harness.View, "EndedAgentsButton");
        Assert.True(endedButton.IsEffectivelyVisible);
        Assert.Contains(endedButton.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Ended");
        Assert.Contains(endedButton.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2");
        await ClickAsync(endedButton);
        var flyout = Assert.IsType<Flyout>(endedButton.Flyout);
        Assert.True(flyout.IsOpen);
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        Assert.True(ActivityButton(content, "running").IsEffectivelyVisible);
        Assert.True(ActivityButton(content, "ended").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(SubagentStatus.Completed, "AlcolerSuccess")]
    [InlineData(SubagentStatus.Failed, "AlcolerError")]
    [InlineData(SubagentStatus.Cancelled, "AlcolerWarning")]
    public async Task EndedOnlyAgents_KeepSummaryAndFlyoutNavigationWithoutRunningCount(SubagentStatus status, string iconBrush)
    {
        using var harness = new Harness(900, Agent("ended", "Finished review", status));
        List<StatusActivity> requested = [];
        harness.View.ActivityRequested += requested.Add;
        await RenderAsync(harness.Window);
        Assert.True(harness.View.IsVisible);
        Assert.False(harness.View.IsExpanded);
        Assert.Equal("1 ended agent", harness.Model.SummaryText);
        var summary = Named<Button>(harness.View, "SummaryButton");
        Assert.Contains(summary.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "1 ended agent");

        await ClickAsync(summary);
        Assert.False(Named<Expander>(harness.View, "AgentsSection").IsExpanded);
        Assert.False(Named<TextBlock>(harness.View, "AgentRunningCount").IsVisible);
        Assert.Equal(string.Empty, Named<TextBlock>(harness.View, "AgentRunningCount").Text);
        await ToggleSectionAsync(harness.View, "AgentsSection");
        var endedButton = Named<Button>(harness.View, "EndedAgentsButton");
        await ClickAsync(endedButton);
        var flyout = Assert.IsType<Flyout>(endedButton.Flyout);
        Assert.True(flyout.IsOpen);
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        var row = ActivityButton(content, "ended");
        var icon = Assert.Single(row.GetVisualDescendants().OfType<Alcoler.Controls.Icon>(),
            control => control.Classes.Contains("agentIcon"));
        AssertThemeBrush(harness.Window, harness.View.ActualThemeVariant, iconBrush, icon.Stroke);
        Assert.DoesNotContain(content.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("stopActivity") && button.IsVisible);

        await ClickAsync(row);
        Assert.False(flyout.IsOpen);
        var activity = Assert.Single(requested);
        Assert.Equal("ended", activity.RowId);
        Assert.Equal(new SessionId("child-ended"), activity.ChildSessionId);
        Assert.Equal(status.ToString(), activity.StatusText);
        Assert.False(activity.CanStop);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task ActiveAgent_ShowsTypedIdentityAndIndependentStopWithoutTranscriptNavigation()
    {
        var agent = Agent("agent", "Inspect production view");
        using var harness = new Harness(1400, agent with { Subagent = agent.Subagent! with { AgentType = "Explore" } });
        List<StatusActivity> requested = [];
        harness.View.ActivityRequested += requested.Add;
        await ToggleSectionAsync(harness.View, "AgentsSection");
        var row = ActivityButton(harness.View, "agent");
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Explore · Running");
        var icon = Assert.Single(row.GetVisualDescendants().OfType<Alcoler.Controls.Icon>(),
            control => control.Classes.Contains("agentIcon"));
        AssertThemeBrush(harness.Window, harness.View.ActualThemeVariant, "AlcolerTextPrimary", icon.Stroke);
        var stop = Assert.Single(harness.View.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("stopActivity") && button.IsEffectivelyVisible);
        Assert.DoesNotContain(row.GetVisualDescendants(), visual => ReferenceEquals(visual, stop));
        await ClickAsync(stop);
        Assert.Empty(requested);
        Assert.True(Named<Expander>(harness.View, "AgentsSection").IsExpanded);
        await ClickAsync(row);
        Assert.Equal(new SessionId("child-agent"), Assert.Single(requested).ChildSessionId);
    }

    [AvaloniaFact]
    public async Task TerminalAgentAndEndedAgentRows_ClickThroughWithStructuredActivityTargets()
    {
        using var harness = new Harness(1400, Terminal("terminal", "Build workbench"),
            Agent("agent", "Inspect production view"), Agent("ended", "Completed review", SubagentStatus.Completed));
        List<StatusActivity> requested = [];
        harness.View.ActivityRequested += requested.Add;
        await ToggleSectionAsync(harness.View, "TerminalsSection");
        await ClickAsync(ActivityButton(harness.View, "terminal"));
        var terminal = Assert.Single(requested);
        Assert.Equal("terminal", terminal.RowId);
        Assert.Equal("Build workbench", terminal.Title);
        Assert.Null(terminal.ChildSessionId);

        await ToggleSectionAsync(harness.View, "AgentsSection");
        await ClickAsync(ActivityButton(harness.View, "agent"));
        Assert.Equal(2, requested.Count);
        Assert.Equal("agent", requested[1].RowId);
        Assert.Equal(new SessionId("child-agent"), requested[1].ChildSessionId);

        var endedButton = Named<Button>(harness.View, "EndedAgentsButton");
        await ClickAsync(endedButton);
        var endedFlyout = Assert.IsType<Flyout>(endedButton.Flyout);
        Assert.True(endedFlyout.IsOpen);
        var flyoutContent = Assert.IsAssignableFrom<Control>(endedFlyout.Content);
        await ClickAsync(ActivityButton(flyoutContent, "ended"));
        Assert.False(endedFlyout.IsOpen);
        Assert.Equal(3, requested.Count);
        Assert.Equal("ended", requested[2].RowId);
        Assert.Equal(new SessionId("child-ended"), requested[2].ChildSessionId);
        Assert.Equal("Completed", requested[2].StatusText);
        Assert.Equal("Finished review", requested[2].OutputText);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task ActivityTimer_StopsForHiddenAncestorEmptyContentAndDetach_AndRestartsOnReattach()
    {
        using var harness = new Harness(900, Terminal("terminal", "Build workbench"));
        await RenderAsync(harness.Window);
        var timerField = Assert.Single(typeof(ConversationStatusView)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => field.FieldType == typeof(DispatcherTimer));
        var timer = Assert.IsType<DispatcherTimer>(timerField.GetValue(harness.View));
        Assert.True(timer.IsEnabled);

        harness.Conversation.IsVisible = false;
        await RenderAsync(harness.Window);
        Assert.False(timer.IsEnabled);
        harness.Conversation.IsVisible = true;
        await RenderAsync(harness.Window);
        Assert.True(timer.IsEnabled);

        Reset(harness.Model, Todo("todos", "No running activities"));
        await RenderAsync(harness.Window);
        Assert.False(timer.IsEnabled);
        Apply(harness.Model, new RowAppended(Terminal("terminal-again", "Run again")));
        await RenderAsync(harness.Window);
        Assert.True(timer.IsEnabled);

        harness.Conversation.Children.Remove(harness.View);
        await RenderAsync(harness.Window);
        Assert.Null(TopLevel.GetTopLevel(harness.View));
        Assert.False(timer.IsEnabled);
        harness.Conversation.Children.Add(harness.View);
        await RenderAsync(harness.Window);
        Assert.True(timer.IsEnabled);
        harness.Window.Close();
        Assert.False(timer.IsEnabled);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Button ActivityButton(Control root, string rowId) =>
        Assert.Single(root.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("activityRow") && button.DataContext is StatusActivity { RowId: var id } && id == rowId);

    private static async Task ToggleSectionAsync(ConversationStatusView view, string name)
    {
        var root = TopLevel.GetTopLevel(view);
        Assert.NotNull(root);
        await RenderAsync(root);
        var section = Named<Expander>(view, name);
        var header = Assert.Single(section.GetVisualDescendants().OfType<ToggleButton>(),
            button => button.Classes.Contains("sectionHeader"));
        var wasExpanded = section.IsExpanded;
        await ClickAsync(header);
        Assert.Equal(!wasExpanded, section.IsExpanded);
    }

    private static void AssertSections(ConversationStatusView view, bool git, bool plans, bool progress, bool terminals, bool agents)
    {
        Assert.Equal(git, Named<Expander>(view, "GitSection").IsExpanded);
        Assert.Equal(plans, Named<Expander>(view, "PlansSection").IsExpanded);
        Assert.Equal(progress, Named<Expander>(view, "ProgressSection").IsExpanded);
        Assert.Equal(terminals, Named<Expander>(view, "TerminalsSection").IsExpanded);
        Assert.Equal(agents, Named<Expander>(view, "AgentsSection").IsExpanded);
    }

    private static void AssertThemeBrush(Window window, ThemeVariant theme, string key, IBrush? actual)
    {
        Assert.True(window.TryFindResource(key, theme, out var expected));
        Assert.Equal(Assert.IsType<SolidColorBrush>(expected).Color, Assert.IsType<SolidColorBrush>(actual).Color);
    }

    private static async Task ClickAsync(Control control)
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
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        await RenderAsync(root);
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

    private static void Reset(ConversationStatusViewModel model, params ConversationRow[] rows) =>
        model.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: rows));

    private static void Apply(ConversationStatusViewModel model, params RowOp[] ops) =>
        model.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(), ops, Reset: false));

    private static ToolCallRow Tool(string rowId, string name, object input, ToolCallStatus status = ToolCallStatus.Succeeded) =>
        new(rowId, StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input), status, null, false, null, StartedAt);

    private static ToolCallRow Todo(string rowId, string content) =>
        Tool(rowId, "TodoWrite", new { todos = new[] { new { content, status = "in_progress" } } });

    private static ToolCallRow Plan(string rowId, string title) =>
        Tool(rowId, "ExitPlanMode", new { plan = $"# {title}\n\nInspect and implement." });

    private static ToolCallRow Terminal(string rowId, string title) =>
        Tool(rowId, "Bash", new { command = "dotnet --info", description = title }, ToolCallStatus.Running);

    private static SystemNoticeRow Agent(string rowId, string title, SubagentStatus status = SubagentStatus.Running) =>
        new(rowId, "subagent", "Typed activity", new SubagentState(new SubagentId(rowId), new SessionId($"child-{rowId}"),
            title, status, status == SubagentStatus.Running ? null : "Finished review", StartedAt,
            status == SubagentStatus.Running ? null : StartedAt.AddMinutes(1)));

    private static ConversationRow[] FullRows() =>
        [Todo("todos", "Inspect status"), Plan("plan", "Implementation plan"),
            Terminal("terminal", "Build workbench"), Agent("agent", "Inspect production view")];

    private static GitPaneViewModel CreateGit() => new(@"C:\status-test", new FakeGitService(), InlineUiScheduler.Instance)
    {
        IsRepo = true,
        CurrentBranch = "main",
        ChangedFileCount = 2,
        AddedLines = 12,
        RemovedLines = 3,
    };

    private sealed class Harness : IDisposable
    {
        private readonly List<ConversationStatusViewModel> _models = [];

        public Harness(double conversationWidth, params ConversationRow[] rows)
        {
            View = new ConversationStatusView { DataContext = CreateModel(rows) };
            Conversation = new Grid { Width = conversationWidth };
            Conversation.Children.Add(View);
            View.Bind(ConversationStatusView.ConversationWidthProperty, new Binding("Bounds.Width") { Source = Conversation });
            Window = new Window
            {
                Width = 1600,
                Height = 800,
                SizeToContent = SizeToContent.Manual,
                Content = Conversation,
            };
            Window.Show();
        }

        public FakeAgentRuntime Runtime { get; } = new();
        public ConversationStatusViewModel Model => Assert.IsType<ConversationStatusViewModel>(View.DataContext);
        public ConversationStatusView View { get; }
        public Grid Conversation { get; }
        public Window Window { get; }

        public ConversationStatusViewModel CreateModel(params ConversationRow[] rows)
        {
            var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
            Runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, rows, TestSnapshots.State()));
            var model = new ConversationStatusViewModel(sessionId, Runtime);
            Reset(model, rows);
            _models.Add(model);
            return model;
        }

        public async Task ResizeConversationAsync(double width)
        {
            Conversation.Width = width;
            await RenderAsync(Window);
        }

        public void Dispose()
        {
            Window.Close();
            foreach (var model in _models)
            {
                model.Dispose();
            }
        }
    }

    private sealed class FakeGitService : IGitService
    {
        public string? FindRepositoryRoot(string workspaceRoot) => throw new NotSupportedException();
        public string? GetCurrentBranchName(string repositoryRoot) => throw new NotSupportedException();
        public GitRepositoryStatus GetStatus(string repositoryRoot) => throw new NotSupportedException();
        public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot) => throw new NotSupportedException();
        public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit) => throw new NotSupportedException();
        public void Checkout(string repositoryRoot, string branchName) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
