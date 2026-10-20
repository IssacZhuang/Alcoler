using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
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

namespace Alcoler.Workbench.Tests.Views;

public class ComposerAndInteractionSmokeTests
{
    private sealed class Harness : IDisposable
    {
        public required ConversationViewModel ViewModel { get; init; }
        public required FakeAgentRuntime Runtime { get; init; }
        public required ProjectionStore Store { get; init; }
        public required Window Window { get; init; }
        public required UserControl View { get; init; }

        public void Dispose()
        {
            Window.Close();
            ViewModel.Dispose();
            Store.Dispose();
        }
    }

    private static Harness Show<TView>(ConversationState? initialState = null) where TView : UserControl, new()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var state = initialState ?? TestSnapshots.State();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], state));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, state);
        var viewModel = new ConversationViewModel(runtime, store);
        var view = new TView { DataContext = viewModel };
        var window = new Window { Content = view };
        window.Show();
        return new Harness
        {
            ViewModel = viewModel,
            Runtime = runtime,
            Store = store,
            Window = window,
            View = view,
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

    [AvaloniaFact]
    public async Task Composer_UsesCompactReferenceDimensions()
    {
        using var harness = Show<ComposerView>(StateWith() with { ContextUsageTokens = 100_000 });
        await PumpAsync();

        var shell = FindNamed<Border>(harness.View, "ComposerShell");
        Assert.Equal(new Avalonia.CornerRadius(16), shell.CornerRadius);
        Assert.Equal(new Avalonia.Thickness(12), shell.Padding);
        Assert.Equal(28, FindNamed<Grid>(harness.View, "ComposerToolbar").Height);
        Assert.Equal(28, FindNamed<Button>(harness.View, "SendButton").Width);
        Assert.Equal(160, FindNamed<TextBox>(harness.View, "InputBox").MaxHeight);
        var ring = FindNamed<Workbench.Controls.Icon>(harness.View, "ContextRingFill");
        Assert.Equal(14, ring.Width);
        Assert.Equal(14, ring.Height);
        Assert.Equal(4, ring.StrokeThickness);
        Assert.Equal(.7, ring.Opacity);
        Assert.Equal(.25, FindNamed<Workbench.Controls.Icon>(harness.View, "ContextRingTrack").Opacity);
        Assert.Equal(28, FindNamed<Button>(harness.View, "ContextTrigger").Width);
    }

    [AvaloniaTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Composer_InputBackgroundStaysTransparentDuringHoverAndFocus(bool light)
    {
        using var harness = Show<ComposerView>();
        var theme = light ? ThemeVariant.Light : ThemeVariant.Dark;
        harness.Window.RequestedThemeVariant = theme;
        harness.Window.Width = 800;
        harness.Window.Height = 600;
        await PumpAsync();
        harness.Window.UpdateLayout();
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        var shell = FindNamed<Border>(harness.View, "ComposerShell");
        var mode = FindNamed<Button>(harness.View, "ModeSelector");
        var template = Assert.Single(input.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "PART_BorderElement");
        var expectedBackground = Assert.IsAssignableFrom<ISolidColorBrush>(harness.View.FindResource(theme, "AlcolerInput")).Color;
        var outside = new Point(1, 1);
        var inside = input.TranslatePoint(new Point(input.Bounds.Width / 2, input.Bounds.Height / 2), harness.Window)!.Value;
        var rendered = ElementComposition.GetElementVisual(harness.Window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        input.Focus();
        await PumpAsync();
        var expectedFocusedBorder = Assert.IsAssignableFrom<ISolidColorBrush>(shell.BorderBrush).Color;

        void AssertBackground()
        {
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(template.Background).Color.A);
            Assert.Equal(expectedBackground, Assert.IsAssignableFrom<ISolidColorBrush>(shell.Background).Color);
        }

        mode.Focus();
        harness.Window.MouseMove(outside);
        await PumpAsync();
        Assert.False(input.IsFocused);
        Assert.False(input.IsPointerOver);
        AssertBackground();

        harness.Window.MouseMove(inside);
        await PumpAsync();
        Assert.True(input.IsPointerOver);
        Assert.False(input.IsFocused);
        AssertBackground();

        harness.Window.MouseDown(inside, MouseButton.Left);
        harness.Window.MouseUp(inside, MouseButton.Left);
        await PumpAsync();
        Assert.True(input.IsFocused);
        AssertBackground();
        Assert.Equal(expectedFocusedBorder, Assert.IsAssignableFrom<ISolidColorBrush>(shell.BorderBrush).Color);

        harness.Window.KeyTextInput("Hover-safe input");
        await PumpAsync();
        Assert.Equal("Hover-safe input", harness.ViewModel.Composer.Draft);
        AssertBackground();

        harness.Window.MouseMove(outside);
        await PumpAsync();
        Assert.False(input.IsPointerOver);
        Assert.True(input.IsFocused);
        AssertBackground();
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaTheory]
    [Xunit.InlineData(600, 600)]
    [Xunit.InlineData(900, 804)]
    [Xunit.InlineData(1400, 1016)]
    public async Task ConversationDockAndInteraction_UseTheSameResponsiveColumn(double paneWidth, double sectionWidth)
    {
        foreach (var interaction in new[] { false, true })
        {
            using var harness = interaction
                ? Show<InteractionAreaView>(StateWith(permission: Permission()))
                : Show<ComposerView>();
            harness.Window.Width = paneWidth;
            harness.Window.Height = 600;
            await PumpAsync();
            harness.Window.UpdateLayout();
            var layout = Assert.Single(harness.View.GetVisualDescendants().OfType<Workbench.Controls.ConversationLayout>());
            var child = Assert.IsAssignableFrom<Control>(layout.Child);
            var origin = child.TranslatePoint(default, harness.View)!.Value;

            Assert.InRange(child.Bounds.Width, sectionWidth - 33, sectionWidth - 31);
            Assert.InRange(origin.X, (paneWidth - sectionWidth) / 2 + 15, (paneWidth - sectionWidth) / 2 + 17);
            Assert.InRange(origin.X + child.Bounds.Width, paneWidth - origin.X - 1, paneWidth - origin.X + 1);
        }
    }

    [AvaloniaFact]
    public async Task Composer_ReasoningPicker_TracksConfiguredModelLevels()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        var picker = FindNamed<ComboBox>(harness.View, "ThoughtLevelSelector");
        Assert.False(picker.IsVisible);
        harness.ViewModel.ConfigureModel(new ModelChoice("test", "reasoning", "Reasoning",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"])));
        await PumpAsync();
        Assert.True(picker.IsVisible);
        Assert.Equal(new[] { "low", "high" }, picker.Items.Cast<string>());
        harness.ViewModel.ConfigureModel(new ModelChoice("test", "plain", "Plain", new Contracts.Models.ModelProperties()));
        await PumpAsync();
        Assert.False(picker.IsVisible);
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_ExplicitModelChoice_UsesHighestReasoningWithoutChangingHydrationDefaults()
    {
        foreach (var draftHost in new[] { false, true })
        foreach (var previousLevel in new[] { "off", "low" })
        {
            using var harness = Show<ComposerView>();
            IComposerHost host;
            if (draftHost)
            {
                host = new DraftConversationViewModel(Environment.CurrentDirectory, null, _ => Task.CompletedTask)
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
            var choice = new ModelChoice("test", "reasoning", "Reasoning",
                new Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"]));
            host.ConfigureModel(choice);
            Assert.Equal("low", host.ThoughtLevel);
            Assert.Empty(harness.Runtime.Submitted);
            var picker = FindNamed<ComboBox>(harness.View, "ModelSelector");
            picker.ItemsSource = new[] { choice };

            picker.SelectedItem = choice;
            await PumpAsync();

            Assert.Equal("high", host.ThoughtLevel);
            if (draftHost)
                Assert.Equal("high", ((DraftConversationViewModel)host).Selection!.Options!.ReasoningLevel);
            else
            {
                var command = Assert.IsType<Contracts.Commands.SwitchModelCommand>(Assert.Single(harness.Runtime.Submitted));
                Assert.Equal("reasoning", command.Selection.ModelId);
                Assert.Equal("high", command.Selection.Options!.ReasoningLevel);
            }
        }
    }

    [AvaloniaFact]
    public async Task Composer_ContextPopover_IsInformationalAndHostsQuota()
    {
        using var harness = Show<ComposerView>();
        var state = StateWith() with
        {
            ContextUsageTokens = 100_000,
            ContextUsage = new ContextUsageState(100_000, 200_000, "provider_usage",
                [new ContextUsageCategory(ContextUsageSources.Messages, 900), new(ContextUsageSources.SystemPrompt, 100)],
                CacheReadTokens: 800, CacheInputTokens: 1000),
        };
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
        await PumpAsync();
        var usage = FindNamed<Workbench.Controls.ContextUsageView>(harness.View, "ContextUsageHost");
        var quota = new TextBlock { Text = "Quota extension" };
        usage.QuotaContent = quota;
        var opened = 0;
        usage.PopoverOpened += (_, _) => opened++;
        var trigger = FindNamed<Button>(harness.View, "ContextTrigger");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpAsync();

        var popup = usage.FindControl<Avalonia.Controls.Primitives.Popup>("ContextPopup")!;
        Assert.True(popup.IsOpen);
        Assert.Equal(1, opened);
        var bridge = Assert.IsType<Border>(popup.Child);
        Assert.Equal(new Avalonia.Thickness(0, 0, 0, 2), bridge.Padding);
        Assert.Equal(0, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(bridge.Background).Color.A);
        var panel = Assert.IsType<Border>(bridge.Child);
        Assert.Equal(320, panel.Width);
        Assert.Equal(new Avalonia.Thickness(12), panel.Padding);
        Assert.Equal(new Avalonia.CornerRadius(12), panel.CornerRadius);
        Assert.Equal(0, popup.VerticalOffset);
        Assert.Equal(8, panel.GetVisualDescendants().OfType<Workbench.Controls.ContextUsageProgress>().Single().Height);
        Assert.Same(quota, panel.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "QuotaHost").Content);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Cache hit rate");
        Assert.Empty(panel.GetVisualDescendants().OfType<Button>());
        Assert.Empty(harness.Runtime.Submitted);
    }

    [AvaloniaFact]
    public async Task Composer_ContextPopover_RealPointerHoverKeepsTriggerAndPanelInteractive()
    {
        using var harness = Show<ComposerView>(StateWith() with { ContextUsageTokens = 100_000 });
        harness.Window.Width = 800;
        harness.Window.Height = 600;
        await PumpAsync();
        var usage = FindNamed<Workbench.Controls.ContextUsageView>(harness.View, "ContextUsageHost");
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

    [AvaloniaTheory]
    [Xunit.InlineData(360, false)]
    [Xunit.InlineData(360, true)]
    [Xunit.InlineData(900, false)]
    [Xunit.InlineData(900, true)]
    public async Task QuestionCard_ResponsiveThemeGeometry_KeepsFooterVisibleAndBodyScrollable(double width, bool light)
    {
        var question = new InteractionQuestion("Destination", "Which destination should receive the complete implementation and its verification changes?",
            Enumerable.Range(1, 18).Select(index => new InteractionOption($"Option {index}", "Secondary description that wraps on a narrow card.")).ToArray());
        using var harness = Show<InteractionAreaView>(QuestionState(question));
        var theme = light ? ThemeVariant.Light : ThemeVariant.Dark;
        harness.Window.RequestedThemeVariant = theme;
        harness.View.MaxHeight = 400;
        await PrepareQuestionAsync(harness, width, 600);
        var card = FindNamed<Border>(harness.View, "QuestionCard");
        var footer = FindNamed<Grid>(harness.View, "QuestionFooter");
        var body = FindNamed<ScrollViewer>(harness.View, "QuestionBody");
        var hint = FindNamed<Grid>(harness.View, "KeyboardHint");
        var actions = FindNamed<StackPanel>(harness.View, "QuestionActions");
        var compact = card.Bounds.Width < 560;
        Assert.Equal(compact ? 2 : 1, Grid.GetColumnSpan(hint));
        Assert.Equal(compact ? 1 : 0, Grid.GetRow(actions));
        if (compact)
            Assert.True(actions.Bounds.Y >= hint.Bounds.Bottom + 7);
        Assert.Equal(new CornerRadius(16), card.CornerRadius);
        Assert.Equal(new Thickness(12), card.Padding);
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(harness.View.FindResource(theme, "AlcolerPopover")).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(card.Background).Color);
        Assert.InRange(card.Bounds.Width, 1, width - 30);
        Assert.InRange(card.Bounds.Height, 100, 400);
        Assert.True(body.Extent.Height > body.Viewport.Height);
        var footerOrigin = footer.TranslatePoint(default, card)!.Value;
        Assert.InRange(footerOrigin.Y + footer.Bounds.Height, 1, card.Bounds.Height - 10);
        foreach (var name in new[] { "DismissQuestionButton", "SubmitAnswersButton" })
        {
            var button = FindNamed<Button>(harness.View, name);
            var origin = button.TranslatePoint(default, card)!.Value;
            Assert.InRange(origin.X, 0, card.Bounds.Width - button.Bounds.Width);
            Assert.InRange(origin.Y, 0, card.Bounds.Height - button.Bounds.Height);
        }
        var option = Option(harness, "Option 1");
        Assert.Equal(new CornerRadius(12), option.CornerRadius);
        Assert.Equal(new Thickness(12, 8), option.Padding);
        Assert.Equal(14, option.GetVisualDescendants().OfType<TextBlock>().Last().FontSize);
        Assert.Empty(harness.Runtime.Submitted);
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
