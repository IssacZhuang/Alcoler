using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

public class ComposerAndInteractionSmokeTests
{
    private sealed class Harness : IDisposable
    {
        public required ConversationViewModel ViewModel { get; init; }
        public required FakeAgentRuntime Runtime { get; init; }
        public required ProjectionStore Store { get; init; }
        public required Window Window { get; init; }
        public required UserControl View { get; init; }
        public required string TempRoot { get; init; }
        public required ComposerPromptHistory History { get; init; }

        public void Dispose()
        {
            Window.Close();
            ViewModel.Dispose();
            Store.Dispose();
            Directory.Delete(TempRoot, recursive: true);
        }
    }

    private static Harness Show<TView>(ConversationState? initialState = null, WorkbenchViewModel? workbench = null) where TView : UserControl, new()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "alcoler-composer-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var history = new ComposerPromptHistory(Path.Combine(tempRoot, "prompt-history.json"));
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var state = initialState ?? TestSnapshots.State();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], state));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, state);
        var viewModel = new ConversationViewModel(runtime, store, new ComposerViewModel(history)) { WorkspaceRoot = tempRoot };
        var view = new TView { DataContext = viewModel };
        var window = new Window { Content = view, DataContext = workbench };
        window.Show();
        return new Harness
        {
            ViewModel = viewModel,
            Runtime = runtime,
            Store = store,
            Window = window,
            View = view,
            TempRoot = tempRoot,
            History = history,
        };
    }

    private static async Task PumpAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static T FindNamed<T>(UserControl root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static ConversationState StateWith(PermissionRequest? permission = null, InteractionRequest? interaction = null) =>
        new(
            "Test session",
            PermissionMode.Build,
            null,
            200_000,
            permission is not null ? TurnPhase.AwaitingPermission : interaction is not null ? TurnPhase.AwaitingInteraction : TurnPhase.Idle,
            null,
            null,
            "estimate",
            [],
            permission,
            interaction);

    private static ConversationState QuestionState(params InteractionQuestion[] questions) =>
        StateWith(interaction: new InteractionRequest(StrongId.NewInteractionId(), "Choose", questions));

    private static Button Option(Harness harness, string label) => harness.View.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Tag is QuestionOptionViewModel option && option.Label == label);

    private static async Task PrepareQuestionAsync(Harness harness, double width = 900, double height = 700)
    {
        harness.Window.Width = width;
        harness.Window.Height = height;
        await PumpAsync();
        harness.Window.UpdateLayout();
        var rendered = ElementComposition.GetElementVisual(harness.Window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        await PumpAsync();
    }

    private static async Task ClickAsync(Harness harness, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), harness.Window)!.Value;
        harness.Window.MouseMove(point);
        harness.Window.MouseDown(point, MouseButton.Left);
        harness.Window.MouseUp(point, MouseButton.Left);
        await PumpAsync();
        harness.Window.UpdateLayout();
    }

    private static async Task PressAsync(Harness harness, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        harness.Window.KeyPressQwerty(key, modifiers);
        harness.Window.KeyReleaseQwerty(key, modifiers);
        await PumpAsync();
        harness.Window.UpdateLayout();
    }

    private static PermissionRequest Permission(string tool = "Write", RiskLevel risk = RiskLevel.Medium) => new(
        StrongId.NewInteractionId(),
        new SessionId(Guid.NewGuid().ToString("N")),
        tool,
        JsonSerializer.SerializeToElement(new { file_path = "a.txt" }),
        risk,
        "test reason");

    [AvaloniaFact]
    public async Task Composer_SendButton_SubmitsTextAndClearsDraft()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        harness.ViewModel.Composer.Draft = "hello workbench";

        var send = FindNamed<Button>(harness.View, "SendButton");
        Assert.True(send.IsEnabled);
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("hello workbench", command.Text);
        Assert.Equal(string.Empty, harness.ViewModel.Composer.Draft);
    }

    [AvaloniaFact]
    public async Task PermissionCard_AllowButton_SubmitsAllowDecision()
    {
        using var harness = Show<InteractionAreaView>();
        await PumpAsync();
        var state = StateWith(permission: Permission("Bash", RiskLevel.High));
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
        await PumpAsync();

        Assert.True(FindNamed<ContentControl>(harness.View, "InteractionHost").IsVisible);
        var permissionCard = FindNamed<Border>(harness.View, "PermissionCard");
        Assert.True(permissionCard.IsVisible);
        var allow = FindNamed<Button>(harness.View, "AllowButton");
        allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal(PermissionDecision.Allow, command.PermissionDecision);
    }

    [AvaloniaFact]
    public async Task PermissionCard_PlanDenialWithFeedback_SubmitsFeedbackFromFocusedTextBox()
    {
        using var harness = Show<InteractionAreaView>();
        await PumpAsync();
        var request = new PermissionRequest(
            StrongId.NewInteractionId(),
            new SessionId(Guid.NewGuid().ToString("N")),
            "ExitPlanMode",
            JsonSerializer.SerializeToElement(new { plan = "1. Add the parser." }),
            RiskLevel.Medium,
            "tool requires user interaction",
            AllowsFeedback: true);
        var state = StateWith(permission: request);
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
        await PumpAsync();

        var feedback = FindNamed<TextBox>(harness.View, "PlanFeedbackBox");
        Assert.True(feedback.IsVisible);
        Assert.Empty(harness.View.GetVisualDescendants().OfType<Expander>());
        var deny = FindNamed<Button>(harness.View, "DenyButton");
        var allow = FindNamed<Button>(harness.View, "AllowButton");
        Assert.Equal(deny.VerticalContentAlignment, allow.VerticalContentAlignment);
        Assert.Equal(deny.FontWeight, allow.FontWeight);
        harness.Window.UpdateLayout();
        var denyText = Assert.Single(deny.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Request changes");
        var allowText = Assert.Single(allow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Approve");
        Assert.Equal(
            denyText.TranslatePoint(new Point(0, denyText.Bounds.Height / 2), harness.Window)!.Value.Y,
            allowText.TranslatePoint(new Point(0, allowText.Bounds.Height / 2), harness.Window)!.Value.Y,
            precision: 1);
        feedback.Focus();
        feedback.Text = "Use the existing parser.";
        await PumpAsync();
        await PressAsync(harness, PhysicalKey.Escape);

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal(PermissionOutcome.Denied, command.PermissionDecision!.Outcome);
        Assert.Equal("Use the existing parser.", command.PermissionDecision.Feedback);
    }

    [AvaloniaFact]
    public async Task QuestionCard_PointerSelectSingleOption_SubmitsImmediately()
    {
        var question = new InteractionQuestion("Destination", "Where should the branch land?",
            [new InteractionOption("main"), new InteractionOption("feature/x")]);
        using var harness = Show<InteractionAreaView>(QuestionState(question));
        await PrepareQuestionAsync(harness);

        Assert.True(FindNamed<Button>(harness.View, "SubmitAnswersButton").IsEnabled);
        Assert.True(FindNamed<Border>(harness.View, "QuestionCard").IsFocused);
        Assert.Empty(harness.ViewModel.PendingInteraction!.SelectedAnswers);
        Assert.Equal(-1, harness.ViewModel.PendingInteraction.ActiveOptionIndex);
        Assert.Equal("1 / 1", FindNamed<TextBlock>(harness.View, "QuestionPage").Text);
        Assert.False(FindNamed<Button>(harness.View, "PreviousQuestionButton").IsEnabled);
        Assert.False(FindNamed<Button>(harness.View, "NextQuestionButton").IsEnabled);

        await ClickAsync(harness, Option(harness, "feature/x"));

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("feature/x", command.InteractionAnswer!.Answers["Destination"]);
    }

    [AvaloniaFact]
    public async Task QuestionCard_PreviewFocusSelectionNotesAndSubmission_PreserveArtifacts()
    {
        var question = new InteractionQuestion("Layout", "Which layout?",
            [new("Split", "Two panes", "```text\n[left][right]\n```"), new("Stack", "One column", "[top]\n[bottom]")]);
        using var harness = Show<InteractionAreaView>(QuestionState(question));
        await PrepareQuestionAsync(harness);

        var preview = FindNamed<Alcoler.Controls.ConversationMarkdown>(harness.View, "QuestionPreview");
        var panel = FindNamed<Border>(harness.View, "QuestionPreviewPanel");
        Assert.True(panel.IsVisible);
        Assert.Contains("[left][right]", preview.Markdown);
        Assert.True(panel.TranslatePoint(default, harness.View)!.Value.X > Option(harness, "Split").TranslatePoint(default, harness.View)!.Value.X);

        Option(harness, "Stack").Focus();
        await PumpAsync();
        Assert.Equal("[top]\n[bottom]", preview.Markdown);
        await ClickAsync(harness, Option(harness, "Stack"));
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Equal("Stack", harness.ViewModel.PendingInteraction!.SelectedAnswers["Layout"]);

        var notes = FindNamed<TextBox>(harness.View, "PreviewNotesInput");
        notes.Focus();
        notes.Text = "Keep generous spacing";
        await PumpAsync();
        Assert.Equal("Keep generous spacing", harness.ViewModel.PendingInteraction.SelectedAnnotations["Layout"].Notes);
        await ClickAsync(harness, FindNamed<Button>(harness.View, "SubmitAnswersButton"));

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("Stack", command.InteractionAnswer!.Answers["Layout"]);
        Assert.Equal("[top]\n[bottom]", command.InteractionAnswer.Annotations!["Layout"].Preview);
        Assert.Equal("Keep generous spacing", command.InteractionAnswer.Annotations["Layout"].Notes);
    }

    [AvaloniaFact]
    public async Task QuestionCard_MultiSelectDoesNotRenderPreviews()
    {
        var question = new InteractionQuestion("Features", "Which features?",
            [new("Search", "Find files", "not supported for multi-select"), new("History", "View changes")], true);
        using var harness = Show<InteractionAreaView>(QuestionState(question));
        await PrepareQuestionAsync(harness);

        Assert.False(FindNamed<Border>(harness.View, "QuestionPreviewPanel").IsVisible);
        Assert.False(FindNamed<TextBox>(harness.View, "PreviewNotesInput").IsVisible);
        Assert.Empty(harness.ViewModel.PendingInteraction!.SelectedAnnotations);
    }

    [AvaloniaFact]
    public async Task Composer_TurnActive_AllowsQueueWithoutChangingPermissionsAndShowsStop()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        var send = FindNamed<Button>(harness.View, "SendButton");
        var stop = FindNamed<Button>(harness.View, "StopButton");
        Assert.False(stop.IsVisible);

        harness.ViewModel.Composer.Draft = "queued thought";
        Assert.True(send.IsEnabled);

        harness.ViewModel.Composer.IsTurnActive = true;
        Assert.True(send.IsEnabled);
        Assert.True(send.IsVisible);
        Assert.False(stop.IsVisible);
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("queued thought", command.Text);
        Assert.Null(command.Mode);
        Assert.Equal(string.Empty, harness.ViewModel.Composer.Draft);
        Assert.True(stop.IsVisible);
        Assert.False(send.IsVisible);
    }

    private static Button QueueAction(Harness harness, string name) =>
        harness.View.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == name);

    [AvaloniaFact]
    public async Task QueueStrip_WithdrawRestoresTextModeAndModelAndRemovesItem()
    {
        var commandId = StrongId.NewCommandId();
        var selection = new ModelSelection("provider", "model");
        var state = TestSnapshots.State() with
        {
            Queue = [new QueuedInput(commandId, "queued text", DateTimeOffset.UtcNow,
                Mode: PermissionMode.Plan, ModelSelection: selection)],
        };
        using var harness = Show<ComposerView>(state);
        await PumpAsync();

        QueueAction(harness, "Withdraw queued message").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpAsync();

        var command = Assert.IsType<Contracts.Commands.RemoveQueuedInputCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal(commandId, command.TargetCommandId);
        Assert.Equal("queued text", harness.ViewModel.Composer.Draft);
        Assert.Equal(PermissionMode.Plan, harness.ViewModel.Composer.Mode);
        Assert.Equal(selection, harness.ViewModel.CurrentSelection);
    }

    [AvaloniaFact]
    public async Task QueueStrip_DeleteRemovesItemWithoutChangingTheDraft()
    {
        var commandId = StrongId.NewCommandId();
        var state = TestSnapshots.State() with
        {
            Queue = [new QueuedInput(commandId, "queued text", DateTimeOffset.UtcNow)],
        };
        using var harness = Show<ComposerView>(state);
        await PumpAsync();

        QueueAction(harness, "Delete queued message").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpAsync();

        var command = Assert.IsType<Contracts.Commands.RemoveQueuedInputCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal(commandId, command.TargetCommandId);
        Assert.Empty(harness.ViewModel.Composer.Draft);
        Assert.False(harness.ViewModel.Composer.HasAttachments);
    }

    [AvaloniaFact]
    public async Task QueueStrip_WithdrawRefusesToOverwriteAnExistingDraft()
    {
        var state = TestSnapshots.State() with
        {
            Queue = [new QueuedInput(StrongId.NewCommandId(), "queued text", DateTimeOffset.UtcNow)],
        };
        using var harness = Show<ComposerView>(state);
        await PumpAsync();
        harness.ViewModel.Composer.Draft = "existing draft";

        QueueAction(harness, "Withdraw queued message").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpAsync();

        Assert.Empty(harness.Runtime.Submitted);
        Assert.Equal("existing draft", harness.ViewModel.Composer.Draft);
        Assert.Contains("Send or clear the current draft", harness.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task Composer_ReasoningPicker_TracksConfiguredModelLevels()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        var picker = FindNamed<Button>(harness.View, "ThoughtLevelSelector");
        Assert.False(picker.IsVisible);
        Assert.Null(picker.Flyout);
        harness.ViewModel.ConfigureModel(new ModelChoice("test", "reasoning", "Reasoning",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"])));
        await PumpAsync();
        Assert.True(picker.IsVisible);
        var thoughtIcon = picker.GetVisualDescendants().OfType<Alcoler.Controls.Icon>()
            .Single(icon => icon.Name != "ThoughtChevron");
        var thoughtLabel = picker.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "low");
        Assert.Equal(thoughtIcon.Bounds.Height, thoughtLabel.Bounds.Height, 2);
        Assert.Equal(thoughtIcon.Bounds.Center.Y, thoughtLabel.Bounds.Center.Y, 2);
        var menu = Assert.IsType<MenuFlyout>(picker.Flyout);
        Assert.True(FindNamed<Alcoler.Controls.Icon>(harness.View, "ThoughtChevron").IsVisible);
        Assert.Equal(new[] { "low", "high" }, menu.Items.OfType<MenuItem>().Select(item => item.Header));
        Assert.True(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "low")).IsChecked);

        picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "high"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await PumpAsync();
        Assert.Equal("high", harness.ViewModel.ThoughtLevel);
        Assert.True(FindNamed<TextBox>(harness.View, "InputBox").IsFocused);

        harness.ViewModel.ConfigureModel(new ModelChoice("test", "fixed", "Fixed",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["medium"])));
        await PumpAsync();
        Assert.True(picker.IsVisible);
        Assert.False(FindNamed<Alcoler.Controls.Icon>(harness.View, "ThoughtChevron").IsVisible);
        Assert.Null(picker.Flyout);
        Assert.Equal("medium", harness.ViewModel.ThoughtLevel);

        harness.ViewModel.ConfigureModel(new ModelChoice("test", "plain", "Plain", new Contracts.Models.ModelProperties()));
        await PumpAsync();
        Assert.False(picker.IsVisible);
        Assert.Null(picker.Flyout);
        Assert.Empty(harness.Runtime.Submitted);
    }

    private static WorkbenchViewModel ModelWorkbench() => new(new FakeAgentRuntime(), InlineUiScheduler.Instance,
        new WorkbenchConfig([
            new WorkbenchProviderConfig("openai-compatible", "https://example.invalid/v1", null,
                [new WorkbenchModelConfig("reasoning", SupportsImage: true, ReasoningLevels: ["low", "high"])],
                Id: "test", Name: "Test provider"),
            new WorkbenchProviderConfig("openai-compatible", "https://other.invalid/v1", null,
                [new WorkbenchModelConfig("plain", ReasoningLevels: [])], Id: "other", Name: "Other provider"),
        ]));

    [AvaloniaFact]
    public async Task Composer_RuntimeModelUpdatesKeepFriendlyProviderLabel()
    {
        const string providerId = "provider-0ff1867c1b0e442a921";
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance,
            new WorkbenchConfig([
                new WorkbenchProviderConfig("openai-compatible", "https://example.invalid/v1", null,
                    [new WorkbenchModelConfig("reasoning", ReasoningLevels: ["low", "high"])],
                    Id: providerId, Name: "ChatGPT"),
            ]));
        using var harness = Show<ComposerView>(workbench: workbench);
        await PrepareQuestionAsync(harness);
        var label = FindNamed<TextBlock>(harness.View, "ModelLabel");
        var picker = FindNamed<Button>(harness.View, "ModelSelector");
        var selection = new Contracts.Models.ModelSelection(providerId, "reasoning",
            new Contracts.Models.ModelCallOptions(ReasoningLevel: "high"));

        foreach (var phase in new[] { TurnPhase.AwaitingModel, TurnPhase.Streaming, TurnPhase.Idle })
        {
            var state = TestSnapshots.State(phase, modelSelection: selection);
            harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            await PumpAsync();
            Assert.Equal("ChatGPT/reasoning", label.Text);
            Assert.Equal("ChatGPT/reasoning", ToolTip.GetTip(picker));
        }

        harness.View.DataContext = new DraftConversationViewModel(harness.TempRoot,
            workbench.ModelChoices.Single(), _ => Task.CompletedTask);
        await PumpAsync();
        Assert.Equal("ChatGPT/reasoning", label.Text);
        harness.View.DataContext = harness.ViewModel;
        await PumpAsync();
        Assert.Equal("ChatGPT/reasoning", label.Text);
    }

    [AvaloniaFact]
    public async Task Composer_ExplicitModelChoice_UsesHighestReasoningWithoutChangingHydrationDefaults()
    {
        foreach (var draftHost in new[] { false, true })
        foreach (var previousLevel in new[] { "off", "low" })
        {
            using var workbench = ModelWorkbench();
            using var harness = Show<ComposerView>(workbench: workbench);
            IComposerHost host;
            if (draftHost)
            {
                host = new DraftConversationViewModel(harness.TempRoot, null, _ => Task.CompletedTask)
                {
                    ThoughtLevel = previousLevel,
                };
                harness.View.DataContext = host;
            }
            else
            {
                host = harness.ViewModel;
                harness.ViewModel.RestoreComposerState("", PermissionMode.Build,
                    new Contracts.Models.ModelSelection("old", "old-model",
                        new Contracts.Models.ModelCallOptions(ReasoningLevel: previousLevel)));
            }
            var choice = workbench.ModelChoices.Single(model => model.ModelId == "reasoning");
            host.ConfigureModel(choice);
            Assert.Equal("low", host.ThoughtLevel);
            Assert.Empty(harness.Runtime.Submitted);
            await PrepareQuestionAsync(harness);
            var picker = FindNamed<Button>(harness.View, "ModelSelector");
            await ClickAsync(harness, picker);
            var menu = Assert.IsType<MenuFlyout>(picker.Flyout);
            Assert.True(menu.IsOpen);
            var provider = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Test provider"));
            var model = provider.Items.OfType<MenuItem>().Single(item => item.Tag is ModelChoice candidate && candidate == choice);
            var header = Assert.IsType<StackPanel>(model.Header);
            Assert.Contains(header.Children.OfType<TextBlock>(), text => text.Text == "Vision");

            model.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await PumpAsync();

            Assert.Equal("high", host.ThoughtLevel);
            Assert.Equal("test", host.CurrentSelection!.ProviderId);
            Assert.Equal("reasoning", host.CurrentSelection.ModelId);
            Assert.Equal("high", host.CurrentSelection.Options!.ReasoningLevel);
            Assert.Empty(harness.Runtime.Submitted);
            Assert.True(FindNamed<TextBox>(harness.View, "InputBox").IsFocused);
            await ClickAsync(harness, picker);
            Assert.True(menu.IsOpen);
            provider = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Test provider"));
            Assert.True(provider.IsChecked);
            Assert.True(provider.Items.OfType<MenuItem>().Single(item => item.Tag is ModelChoice selected && selected == choice).IsChecked);
            Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Other provider")).IsChecked);
            Assert.Equal("Manage models", menu.Items.OfType<MenuItem>().Last().Header);
            menu.Hide();

            if (!draftHost)
            {
                host.Composer.Draft = "send selected model";
                FindNamed<Button>(harness.View, "SendButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
                Assert.Equal(host.CurrentSelection, command.ModelSelection);
                Assert.Equal("send selected model", command.Text);
            }
        }
    }

    [AvaloniaTheory]
    [Xunit.InlineData(PhysicalKey.ArrowUp)]
    [Xunit.InlineData(PhysicalKey.ArrowDown)]
    public async Task Composer_HistoryKeyboardStartsAtNewestClampsOldestAndRestoresEmpty(PhysicalKey firstKey)
    {
        using var harness = Show<ComposerView>();
        harness.ViewModel.Composer.RecordSent("oldest");
        harness.ViewModel.Composer.RecordSent("newest");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();

        await PressAsync(harness, firstKey);
        Assert.Equal("newest", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("oldest", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("oldest", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.Equal("newest", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.Equal(string.Empty, input.Text);
        Assert.Equal(string.Empty, harness.ViewModel.Composer.Draft);
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Equal(["oldest", "newest"], harness.History.GetEntries(harness.TempRoot));
    }

    [AvaloniaFact]
    public async Task Composer_HistoryTypingExitsBrowsingAndDoesNotOverwriteANonemptyDraft()
    {
        using var harness = Show<ComposerView>();
        harness.ViewModel.Composer.RecordSent("oldest");
        harness.ViewModel.Composer.RecordSent("newest");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("newest", input.Text);

        harness.Window.KeyTextInput(" edited");
        await PumpAsync();
        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("newest edited", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.Equal("newest edited", input.Text);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_MultilineArrowsPreserveTextAndShiftEnterInsertsANewline()
    {
        using var harness = Show<ComposerView>();
        harness.ViewModel.Composer.RecordSent("history must not replace draft");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        input.Text = "first line\nsecond line";
        input.CaretIndex = input.Text.Length;

        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("first line\nsecond line", input.Text);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.Equal("first line\nsecond line", input.Text);
        input.CaretIndex = input.Text.Length;
        await PressAsync(harness, PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal("first line\nsecond line\n", input.Text!.Replace("\r\n", "\n"));
        Assert.Empty(harness.Runtime.Submitted);
    }

    private static string WriteComposerSkill(Harness harness, string name = "demo")
    {
        var root = Path.Combine(harness.TempRoot, "skill-workspace");
        var directory = Path.Combine(root, ".alcoler", "skills", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: A test composer skill.\n---\nFollow test skill instructions.");
        harness.ViewModel.WorkspaceRoot = root;
        return root;
    }

    [AvaloniaTheory]
    [Xunit.InlineData(PhysicalKey.Enter)]
    [Xunit.InlineData(PhysicalKey.Tab)]
    public async Task Composer_SlashSkillCompletionReplacesTokenWithoutSending(PhysicalKey completionKey)
    {
        using var harness = Show<ComposerView>();
        var root = WriteComposerSkill(harness);
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.Window.KeyTextInput("/demo");
        await PumpAsync();
        var panel = FindNamed<Border>(harness.View, "SkillPanel");
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        Assert.True(panel.IsVisible);
        Assert.Contains(suggestions.Items.OfType<ComposerSkillEntry>(), entry => entry.Name == "demo"
            && entry.Path == Path.Combine(root, ".alcoler", "skills", "demo", "SKILL.md"));
        suggestions.SelectedItem = suggestions.Items.OfType<ComposerSkillEntry>().Single(entry => entry.Name == "demo");

        await PressAsync(harness, completionKey);

        Assert.Equal("/demo ", input.Text);
        Assert.Equal("/demo ", harness.ViewModel.Composer.Draft);
        Assert.False(panel.IsVisible);
        Assert.True(input.IsFocused);
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Empty(harness.History.GetEntries(root));

        harness.Window.KeyTextInput("inspect this workspace");
        await PressAsync(harness, PhysicalKey.Enter);
        var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("/demo inspect this workspace", command.Text);
        Assert.DoesNotContain("<command-name>", command.Text);
        Assert.Equal([command.Text], harness.History.GetEntries(root));
    }

    [AvaloniaFact]
    public async Task Composer_PlanSuggestionCompletesToCommandWithoutSending()
    {
        using var harness = Show<ComposerView>();
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.Window.KeyTextInput("/pl");
        await PumpAsync();
        var panel = FindNamed<Border>(harness.View, "SkillPanel");
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        Assert.True(panel.IsVisible);
        var plan = Assert.IsType<ComposerSkillEntry>(suggestions.SelectedItem);
        Assert.True(plan.IsCommand);
        Assert.Equal("plan", plan.Name);

        await PressAsync(harness, PhysicalKey.Tab);

        Assert.Equal("/plan ", input.Text);
        Assert.Equal("/plan ", harness.ViewModel.Composer.Draft);
        Assert.False(panel.IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_PlanCommandShadowsSkillWithSameName()
    {
        using var harness = Show<ComposerView>();
        WriteComposerSkill(harness, "plan");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.Window.KeyTextInput("/plan");
        await PumpAsync();
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        var plan = Assert.Single(suggestions.Items.OfType<ComposerSkillEntry>(),
            entry => entry.Name.Equals("plan", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.IsCommand);
        Assert.Equal(string.Empty, plan.Path);
    }

    [AvaloniaFact]
    public async Task Composer_PlanMode_ShowsBadgeOnlyWhilePlanIsSelected()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        var badge = FindNamed<Border>(harness.View, "PlanModeBadge");
        Assert.False(badge.IsVisible);

        harness.ViewModel.Composer.Mode = PermissionMode.Plan;
        await PumpAsync();
        Assert.True(badge.IsVisible);
        Assert.Contains(badge.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plan mode");

        harness.ViewModel.Composer.Mode = PermissionMode.Edit;
        await PumpAsync();
        Assert.False(badge.IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_PlanCommandWithPrompt_SendsPromptInPlanMode()
    {
        using var harness = Show<ComposerView>();
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        input.Text = "/plan design the feature";
        input.CaretIndex = input.Text.Length;
        input.SelectionStart = input.CaretIndex;
        input.SelectionEnd = input.CaretIndex;
        await PumpAsync();

        await PressAsync(harness, PhysicalKey.Enter);

        var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("design the feature", command.Text);
        Assert.Equal(PermissionMode.Plan, command.Mode);
        Assert.Equal(PermissionMode.Plan, harness.ViewModel.Composer.Mode);
        Assert.True(FindNamed<Border>(harness.View, "PlanModeBadge").IsVisible);
        Assert.Equal(string.Empty, harness.ViewModel.Composer.Draft);
    }

    [AvaloniaFact]
    public async Task Composer_SlashSuggestionPointerClickPreservesInputAndDoesNotSubmit()
    {
        using var harness = Show<ComposerView>();
        var root = WriteComposerSkill(harness, "alcoler-pointer-a");
        WriteComposerSkill(harness, "alcoler-pointer-b");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        input.Text = "Before /alcoler-pointer-\nAfter";
        input.CaretIndex = "Before /alcoler-pointer-".Length;
        input.SelectionStart = input.CaretIndex;
        input.SelectionEnd = input.CaretIndex;
        await PrepareQuestionAsync(harness);
        var panel = FindNamed<Border>(harness.View, "SkillPanel");
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        Assert.True(panel.IsVisible);
        Assert.Equal("alcoler-pointer-a", Assert.IsType<ComposerSkillEntry>(suggestions.SelectedItem).Name);
        var item = suggestions.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(candidate => candidate.DataContext is ComposerSkillEntry { Name: "alcoler-pointer-b" });
        Assert.True(item.IsVisible);
        Assert.True(item.Bounds.Width > 0 && item.Bounds.Height > 0);
        Assert.Equal(32, item.Bounds.Height);

        await ClickAsync(harness, item);

        Assert.Equal("Before /alcoler-pointer-b \nAfter", input.Text);
        Assert.Equal(input.Text, harness.ViewModel.Composer.Draft);
        Assert.Equal("Before /alcoler-pointer-b ".Length, input.CaretIndex);
        Assert.True(input.IsFocused);
        Assert.False(panel.IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Empty(harness.History.GetEntries(root));
    }

    [AvaloniaFact]
    public async Task Composer_SlashSkillKeyboardNavigationAndEscapeDoNotSubmit()
    {
        using var harness = Show<ComposerView>();
        WriteComposerSkill(harness, "alcoler-test-a");
        WriteComposerSkill(harness, "alcoler-test-b");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.Window.KeyTextInput("/alcoler-test-");
        await PumpAsync();
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        var panel = FindNamed<Border>(harness.View, "SkillPanel");
        Assert.True(panel.IsVisible);
        Assert.Equal("alcoler-test-a", Assert.IsType<ComposerSkillEntry>(suggestions.SelectedItem).Name);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.Equal("alcoler-test-b", Assert.IsType<ComposerSkillEntry>(suggestions.SelectedItem).Name);
        await PressAsync(harness, PhysicalKey.ArrowUp);
        Assert.Equal("alcoler-test-a", Assert.IsType<ComposerSkillEntry>(suggestions.SelectedItem).Name);

        await PressAsync(harness, PhysicalKey.Escape);

        Assert.False(panel.IsVisible);
        Assert.Equal("/alcoler-test-", input.Text);
        Assert.True(input.IsFocused);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_SlashCompletionAfterWhitespacePreservesSurroundingText()
    {
        using var harness = Show<ComposerView>();
        WriteComposerSkill(harness);
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        input.Text = "Before\n/demo preserve this suffix";
        input.CaretIndex = "Before\n/demo".Length;
        input.SelectionStart = input.CaretIndex;
        input.SelectionEnd = input.CaretIndex;
        await PumpAsync();
        var panel = FindNamed<Border>(harness.View, "SkillPanel");
        var suggestions = FindNamed<ListBox>(harness.View, "SkillSuggestions");
        Assert.True(panel.IsVisible);
        suggestions.SelectedItem = suggestions.Items.OfType<ComposerSkillEntry>().Single(entry => entry.Name == "demo");

        await PressAsync(harness, PhysicalKey.Tab);

        Assert.StartsWith("Before\n/demo ", input.Text);
        Assert.EndsWith("preserve this suffix", input.Text);
        Assert.Equal(input.Text, harness.ViewModel.Composer.Draft);
        Assert.False(panel.IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaTheory]
    [Xunit.InlineData("https://demo.example/path")]
    [Xunit.InlineData("https://example.test/demo")]
    [Xunit.InlineData("folder/demo")]
    [Xunit.InlineData("/demo/path")]
    public async Task Composer_UrlsAndPathsDoNotOpenSlashSuggestions(string text)
    {
        using var harness = Show<ComposerView>();
        WriteComposerSkill(harness);
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.Window.KeyTextInput(text);
        await PumpAsync();

        Assert.Equal(text, input.Text);
        Assert.False(FindNamed<Border>(harness.View, "SkillPanel").IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_HistoryRecallOfSlashTokenDoesNotReopenSuggestions()
    {
        using var harness = Show<ComposerView>();
        WriteComposerSkill(harness);
        harness.ViewModel.Composer.RecordSent("/demo");
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();

        await PressAsync(harness, PhysicalKey.ArrowUp);

        Assert.Equal("/demo", input.Text);
        Assert.False(FindNamed<Border>(harness.View, "SkillPanel").IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_ControlTCyclesSupportedReasoningLocallyAndWraps()
    {
        using var harness = Show<ComposerView>();
        var choice = new ModelChoice("test", "reasoning", "Reasoning",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["off", "low", "high"]));
        harness.ViewModel.ConfigureModel(choice);
        await harness.ViewModel.SwitchModelAsync(harness.ViewModel.ApplyThoughtLevel(choice.Selection));
        await PrepareQuestionAsync(harness);
        FindNamed<TextBox>(harness.View, "InputBox").Focus();

        foreach (var expected in new[] { "low", "high", "off" })
        {
            await PressAsync(harness, PhysicalKey.T, RawInputModifiers.Control);
            Assert.Equal(expected, harness.ViewModel.ThoughtLevel);
            Assert.Equal(expected, harness.ViewModel.CurrentSelection!.Options!.ReasoningLevel);
        }
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_ImeCompositionDoesNotSubmitOrCycleReasoning()
    {
        using var harness = Show<ComposerView>();
        harness.ViewModel.ConfigureModel(new ModelChoice("test", "reasoning", "Reasoning",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"])));
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        harness.ViewModel.Composer.Draft = "draft ";
        var presenter = Assert.Single(input.GetVisualDescendants().OfType<TextPresenter>());
        presenter.PreeditText = "composition";

        await PressAsync(harness, PhysicalKey.Enter);
        await PressAsync(harness, PhysicalKey.T, RawInputModifiers.Control);
        await PressAsync(harness, PhysicalKey.Escape);

        Assert.Empty(harness.Runtime.Submitted);
        Assert.Equal("low", harness.ViewModel.ThoughtLevel);
        presenter.PreeditText = null;
        harness.ViewModel.Composer.Draft = string.Empty;
        input.CaretIndex = 0;
        harness.Window.KeyTextInput("Committed message");
        await PressAsync(harness, PhysicalKey.Enter);
        var command = Assert.IsType<Contracts.Commands.SendTextCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("Committed message", command.Text);
    }

    [AvaloniaFact]
    public async Task Composer_ClipboardTextPasteUsesControlVAndReplacesSelection()
    {
        using var harness = Show<ComposerView>();
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        input.Focus();
        input.Text = "abcdefgh";
        input.CaretIndex = 6;
        input.SelectionStart = 2;
        input.SelectionEnd = 6;
        var clipboard = Assert.IsAssignableFrom<Avalonia.Input.Platform.IClipboard>(harness.Window.Clipboard);
        await clipboard.SetTextAsync("pasted text");
        try
        {
            await PressAsync(harness, PhysicalKey.V, RawInputModifiers.Control);
            await PumpAsync();
            await PumpAsync();

            Assert.Equal("abpasted textgh", input.Text);
            Assert.Equal(input.Text, harness.ViewModel.Composer.Draft);
            Assert.Empty(harness.ViewModel.Composer.Attachments);
            Assert.Empty(harness.Runtime.Submitted);
        }
        finally
        {
            await clipboard.ClearAsync();
        }
    }

    [AvaloniaFact]
    public async Task Composer_ContextPopover_RealPointerHoverKeepsTriggerAndPanelInteractive()
    {
        using var harness = Show<ComposerView>(StateWith() with { ContextUsageTokens = 100_000 });
        harness.Window.Width = 800;
        harness.Window.Height = 600;
        await PumpAsync();
        var usage = FindNamed<Alcoler.Controls.ContextUsageView>(harness.View, "ContextUsageHost");
        var trigger = FindNamed<Button>(harness.View, "ContextTrigger");
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        var popup = usage.FindControl<Avalonia.Controls.Primitives.Popup>("ContextPopup")!;
        input.Focus();
        var outside = new Avalonia.Point(1, 1);

        foreach (var overlay in new[] { false, true })
        {
            popup.ShouldUseOverlayLayer = overlay;
            harness.Window.MouseMove(outside);
            await PumpAsync();
            var triggerPoint = trigger.TranslatePoint(new Avalonia.Point(14, 14), harness.Window)!.Value;
            harness.Window.MouseMove(triggerPoint);
            await PumpAsync();
            harness.Window.MouseMove(triggerPoint + new Avalonia.Vector(1, 0));
            await PumpAsync();

            Assert.True(trigger.IsPointerOver);
            Assert.True(popup.IsOpen);
            Assert.False(popup.IsLightDismissEnabled);
            Assert.False(trigger.IsFocused);
            Assert.True(input.IsFocused);

            var bridge = Assert.IsType<Border>(popup.Child);
            var popupTopLevel = TopLevel.GetTopLevel(bridge)!;
            var bridgePoint = bridge.TranslatePoint(new Avalonia.Point(bridge.Bounds.Width - 14, bridge.Bounds.Height - 1), popupTopLevel)!.Value;
            popupTopLevel.MouseMove(bridgePoint);
            await PumpAsync();
            Assert.True(bridge.IsPointerOver);
            Assert.True(popup.IsOpen);

            popupTopLevel.MouseMove(new Avalonia.Point(-10, -10));
            harness.Window.MouseMove(outside);
            await PumpAsync();
            Assert.False(popup.IsOpen);

            harness.Window.MouseMove(triggerPoint);
            await PumpAsync();
            Assert.True(popup.IsOpen);
            harness.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            harness.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await PumpAsync();
            Assert.False(popup.IsOpen);
            Assert.True(input.IsFocused);
        }
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task QuestionCard_PageNavigationAndStatePatch_PreserveDraftsAndCurrentPage()
    {
        var state = QuestionState(
            new InteractionQuestion("Target", "Choose target", [new InteractionOption("main"), new InteractionOption("branch")]),
            new InteractionQuestion("Features", "Choose features", [new InteractionOption("Search")], MultiSelect: true));
        using var harness = Show<InteractionAreaView>(state);
        await PrepareQuestionAsync(harness);
        var pending = harness.ViewModel.PendingInteraction!;

        await ClickAsync(harness, Option(harness, "main"));
        Assert.Equal(1, pending.CurrentQuestionIndex);
        Assert.Equal("2 / 2", FindNamed<TextBlock>(harness.View, "QuestionPage").Text);
        Assert.DoesNotContain(harness.View.GetVisualDescendants().OfType<Button>(), button =>
            button.Tag is QuestionOptionViewModel { Label: "main" });
        await ClickAsync(harness, Option(harness, "Search"));
        await ClickAsync(harness, FindNamed<TextBox>(harness.View, "CustomAnswerInput"));
        harness.Window.KeyTextInput("  Offline  ");
        await PumpAsync();
        Assert.Equal("Search, Offline", pending.SelectedAnswers["Features"]);

        var refreshed = state with { PendingInteraction = state.PendingInteraction! with { Prompt = "Updated prompt" } };
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, refreshed, [new StatePatched(refreshed)], Reset: false));
        await PumpAsync();
        Assert.Same(pending, harness.ViewModel.PendingInteraction);
        Assert.Equal(1, pending.CurrentQuestionIndex);

        await PressAsync(harness, PhysicalKey.Escape);
        Assert.Equal(0, pending.CurrentQuestionIndex);
        Assert.True(((QuestionOptionViewModel)Option(harness, "main").Tag!).IsSelected);
        await ClickAsync(harness, FindNamed<Button>(harness.View, "NextQuestionButton"));
        Assert.Equal(1, pending.CurrentQuestionIndex);
        Assert.Equal("  Offline  ", FindNamed<TextBox>(harness.View, "CustomAnswerInput").Text);
        Assert.True(((QuestionOptionViewModel)Option(harness, "Search").Tag!).IsSelected);
        Assert.Empty(harness.Runtime.Submitted);

        await ClickAsync(harness, FindNamed<Button>(harness.View, "SubmitAnswersButton"));
        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("main", command.InteractionAnswer!.Answers["Target"]);
        Assert.Equal("Search, Offline", command.InteractionAnswer.Answers["Features"]);
    }

    [AvaloniaFact]
    public async Task QuestionCard_KeyboardNavigatesChoicesAndCustomSingleReplacesPreset()
    {
        using var harness = Show<InteractionAreaView>(QuestionState(
            new InteractionQuestion("Target", "Choose target", [new InteractionOption("main"), new InteractionOption("branch")]),
            new InteractionQuestion("Next", "Continue?", [new InteractionOption("Done")])));
        await PrepareQuestionAsync(harness);
        var pending = harness.ViewModel.PendingInteraction!;

        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.True(Option(harness, "main").IsFocused);
        Assert.Empty(pending.SelectedAnswers);
        await PressAsync(harness, PhysicalKey.ArrowRight);
        Assert.True(Option(harness, "branch").IsFocused);
        await ClickAsync(harness, FindNamed<Button>(harness.View, "SubmitAnswersButton"));
        Assert.Equal("branch", pending.SelectedAnswers["Target"]);
        Assert.Equal(1, pending.CurrentQuestionIndex);
        await PressAsync(harness, PhysicalKey.Escape);
        Assert.Equal(0, pending.CurrentQuestionIndex);
        await PressAsync(harness, PhysicalKey.Tab);
        var input = FindNamed<TextBox>(harness.View, "CustomAnswerInput");
        Assert.True(input.IsFocused);
        harness.Window.KeyTextInput("  release branch  ");
        await PumpAsync();
        Assert.Equal("release branch", pending.SelectedAnswers["Target"]);
        Assert.False(((QuestionOptionViewModel)Option(harness, "branch").Tag!).IsSelected);
        await PressAsync(harness, PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(1, pending.CurrentQuestionIndex);
        await PressAsync(harness, PhysicalKey.Enter);

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("release branch", command.InteractionAnswer!.Answers["Target"]);
        Assert.Single(command.InteractionAnswer.Answers);
    }

    [AvaloniaFact]
    public async Task QuestionCard_MultiSpaceTogglesAndEnterAdvancesWithoutSelectingFocusedChoice()
    {
        using var harness = Show<InteractionAreaView>(QuestionState(
            new InteractionQuestion("Features", "Choose features", [new InteractionOption("Search"), new InteractionOption("History")], true),
            new InteractionQuestion("Next", "Continue?", [new InteractionOption("Done")])));
        await PrepareQuestionAsync(harness);
        var pending = harness.ViewModel.PendingInteraction!;

        await PressAsync(harness, PhysicalKey.Tab);
        await PressAsync(harness, PhysicalKey.Space);
        Assert.Equal("Search", pending.SelectedAnswers["Features"]);
        Assert.Equal(0, pending.CurrentQuestionIndex);
        await PressAsync(harness, PhysicalKey.ArrowDown);
        Assert.True(Option(harness, "History").IsFocused);
        await PressAsync(harness, PhysicalKey.Enter);
        Assert.Equal(1, pending.CurrentQuestionIndex);
        Assert.Equal("Search", pending.SelectedAnswers["Features"]);
        await PressAsync(harness, PhysicalKey.Escape);
        Assert.Equal(0, pending.CurrentQuestionIndex);
        await PressAsync(harness, PhysicalKey.Space);
        Assert.Empty(pending.SelectedAnswers);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task QuestionCard_CustomControlEnterSubmitsAllDraftsFromIntermediatePage(bool multiSelect)
    {
        using var harness = Show<InteractionAreaView>(QuestionState(
            new InteractionQuestion("Target", "Choose target", [new InteractionOption("main")], multiSelect),
            new InteractionQuestion("Next", "Continue?", [new InteractionOption("Done")])));
        await PrepareQuestionAsync(harness);
        if (multiSelect)
            await ClickAsync(harness, Option(harness, "main"));
        await ClickAsync(harness, FindNamed<TextBox>(harness.View, "CustomAnswerInput"));
        harness.Window.KeyTextInput("  custom target  ");
        await PumpAsync();
        await PressAsync(harness, PhysicalKey.Enter, RawInputModifiers.Control);

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal(multiSelect ? "main, custom target" : "custom target", command.InteractionAnswer!.Answers["Target"]);
        Assert.Single(command.InteractionAnswer.Answers);
    }

    [AvaloniaTheory]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("partial")]
    [Xunit.InlineData("dismiss")]
    [Xunit.InlineData("escape")]
    [Xunit.InlineData("no-questions")]
    public async Task QuestionCard_SkipOrDismiss_DoesNotInventAnswers(string action)
    {
        var questions = action == "no-questions" ? Array.Empty<InteractionQuestion>() :
        [new InteractionQuestion("Target", "Choose target", [new InteractionOption("main")]),
         new InteractionQuestion("Next", "Continue?", [new InteractionOption("Done")])];
        using var harness = Show<InteractionAreaView>(QuestionState(questions));
        await PrepareQuestionAsync(harness);
        if (action == "partial")
            await ClickAsync(harness, Option(harness, "main"));
        else if (action is "dismiss" or "escape")
        {
            harness.ViewModel.SelectInteractionAnswer("Target", "main");
            if (action == "dismiss")
                await ClickAsync(harness, FindNamed<Button>(harness.View, "DismissQuestionButton"));
            else
                await PressAsync(harness, PhysicalKey.Escape);
        }
        else if (action == "empty")
            await PressAsync(harness, PhysicalKey.Enter);
        if (action is "empty" or "partial" or "no-questions")
            await PressAsync(harness, PhysicalKey.Enter);

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        if (action == "partial")
            Assert.Equal("main", Assert.Single(command.InteractionAnswer!.Answers).Value);
        else
            Assert.Empty(command.InteractionAnswer!.Answers);
    }

    [AvaloniaFact]
    public async Task QuestionCard_CustomImeComposition_DoesNotNavigateOrSubmit()
    {
        using var harness = Show<InteractionAreaView>(QuestionState(
            new InteractionQuestion("Target", "Choose target", [new InteractionOption("main")])));
        await PrepareQuestionAsync(harness);
        var input = FindNamed<TextBox>(harness.View, "CustomAnswerInput");
        await ClickAsync(harness, input);
        var presenter = Assert.Single(input.GetVisualDescendants().OfType<TextPresenter>());
        presenter.PreeditText = "composition";
        await PressAsync(harness, PhysicalKey.Enter);
        await PressAsync(harness, PhysicalKey.Escape);
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Equal(0, harness.ViewModel.PendingInteraction!.CurrentQuestionIndex);
        presenter.PreeditText = null;
        harness.Window.KeyTextInput("Committed answer");
        await PressAsync(harness, PhysicalKey.Enter);
        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("Committed answer", command.InteractionAnswer!.Answers["Target"]);
    }

    [AvaloniaFact]
    public async Task QuestionCard_MultiSelectAccumulatesAndRemovesAnswers()
    {
        var question = new InteractionQuestion("Features", "Which features?",
            [new InteractionOption("Search", "Find files quickly"), new InteractionOption("History")], MultiSelect: true);
        using var harness = Show<InteractionAreaView>(QuestionState(question));
        await PrepareQuestionAsync(harness);
        var pending = harness.ViewModel.PendingInteraction!;
        var submit = FindNamed<Button>(harness.View, "SubmitAnswersButton");
        Assert.True(submit.IsEnabled);

        await ClickAsync(harness, Option(harness, "History"));
        await ClickAsync(harness, Option(harness, "Search"));
        Assert.Equal("Search, History", pending.SelectedAnswers["Features"]);
        Assert.Empty(harness.Runtime.Submitted);
        Assert.Contains(Option(harness, "Search").GetVisualDescendants().OfType<TextBlock>(),
            text => text.Inlines!.OfType<Run>().Any(run => run.Text == "  Find files quickly"));

        await ClickAsync(harness, Option(harness, "Search"));
        await ClickAsync(harness, Option(harness, "History"));
        Assert.Empty(pending.SelectedAnswers);
        Assert.True(submit.IsEnabled);

        await ClickAsync(harness, Option(harness, "Search"));
        await ClickAsync(harness, submit);
        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("Search", command.InteractionAnswer!.Answers["Features"]);
    }
}
