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

        var preview = FindNamed<Workbench.Controls.ConversationMarkdown>(harness.View, "QuestionPreview");
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

    [AvaloniaFact]
    public async Task Composer_HiddenOptionalContentDoesNotReserveSpace()
    {
        using var harness = Show<ComposerView>();
        await PrepareQuestionAsync(harness);
        var shell = FindNamed<Border>(harness.View, "ComposerShell");
        var input = FindNamed<TextBox>(harness.View, "InputBox");
        var toolbar = FindNamed<Grid>(harness.View, "ComposerToolbar");
        var inset = shell.Padding.Top + shell.BorderThickness.Top;

        void AssertCompactLayout()
        {
            Assert.Equal(inset, input.TranslatePoint(default, shell)!.Value.Y);
            Assert.Equal(12, toolbar.TranslatePoint(default, shell)!.Value.Y
                - input.TranslatePoint(default, shell)!.Value.Y - input.Bounds.Height);
            Assert.Equal(shell.Padding.Top + shell.Padding.Bottom + shell.BorderThickness.Top
                + shell.BorderThickness.Bottom + input.Bounds.Height + 12 + toolbar.Bounds.Height, shell.Bounds.Height);
        }

        Assert.False(harness.ViewModel.Composer.HasAttachments);
        AssertCompactLayout();
        var compactHeight = shell.Bounds.Height;
        harness.ViewModel.Composer.InputError = "Could not paste image.";
        await PumpAsync();
        harness.Window.UpdateLayout();
        Assert.True(shell.Bounds.Height > compactHeight);
        harness.ViewModel.Composer.InputError = null;
        await PumpAsync();
        harness.Window.UpdateLayout();
        AssertCompactLayout();
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
        var picker = FindNamed<Button>(harness.View, "ThoughtLevelSelector");
        Assert.False(picker.IsVisible);
        Assert.Null(picker.Flyout);
        harness.ViewModel.ConfigureModel(new ModelChoice("test", "reasoning", "Reasoning",
            new Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"])));
        await PumpAsync();
        Assert.True(picker.IsVisible);
        var menu = Assert.IsType<MenuFlyout>(picker.Flyout);
        Assert.True(FindNamed<Workbench.Controls.Icon>(harness.View, "ThoughtChevron").IsVisible);
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
        Assert.False(FindNamed<Workbench.Controls.Icon>(harness.View, "ThoughtChevron").IsVisible);
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
