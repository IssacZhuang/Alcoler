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
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private static PermissionRequest Permission(string tool = "WriteFile", RiskLevel risk = RiskLevel.Medium) => new(
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
    public async Task QuestionCard_SelectOptionAndSubmit_SendsAnswer()
    {
        using var harness = Show<InteractionAreaView>();
        await PumpAsync();
        var question = new InteractionQuestion(
            "Destination",
            "Where should the branch land?",
            [new InteractionOption("main"), new InteractionOption("feature/x")],
            MultiSelect: false);
        var interaction = new InteractionRequest(StrongId.NewInteractionId(), "Pick a target", [question]);
        var state = StateWith(interaction: interaction);
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
        await PumpAsync();

        var submit = FindNamed<Button>(harness.View, "SubmitAnswersButton");
        Assert.False(submit.IsEnabled);

        var option = harness.View.GetVisualDescendants()
            .OfType<RadioButton>()
            .Single(radio => radio.Content as string == "feature/x");
        option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("feature/x", harness.ViewModel.PendingInteraction!.SelectedAnswers["Destination"]);
        Assert.True(submit.IsEnabled);
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.NotNull(command.InteractionAnswer);
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
    public async Task QuestionCard_MultiSelectAccumulatesAndRemovesAnswers()
    {
        using var harness = Show<InteractionAreaView>();
        await PumpAsync();
        var question = new InteractionQuestion("Features", "Which features?",
            [new InteractionOption("Search", "Find files quickly"), new InteractionOption("History")], MultiSelect: true);
        var interaction = new InteractionRequest(StrongId.NewInteractionId(), "Choose features", [question]);
        var state = StateWith(interaction: interaction);
        harness.ViewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
        await PumpAsync();

        var submit = FindNamed<Button>(harness.View, "SubmitAnswersButton");
        var options = harness.View.GetVisualDescendants().OfType<CheckBox>().Where(option => option.IsVisible).ToArray();
        Assert.Equal(2, options.Length);
        Assert.False(submit.IsEnabled);
        foreach (var option in options.Reverse())
        {
            option.IsChecked = true;
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Assert.Equal("Search, History", harness.ViewModel.PendingInteraction!.SelectedAnswers["Features"]);
        Assert.True(submit.IsEnabled);
        Assert.Contains(harness.View.GetVisualDescendants(), control => control is TextBlock { Text: "Find files quickly" });

        foreach (var option in options)
        {
            option.IsChecked = false;
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Assert.Empty(harness.ViewModel.PendingInteraction.SelectedAnswers);
        Assert.False(submit.IsEnabled);

        options[0].IsChecked = true;
        options[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var command = Assert.IsType<Contracts.Commands.ResolveInteractionCommand>(Assert.Single(harness.Runtime.Submitted));
        Assert.Equal("Search", command.InteractionAnswer!.Answers["Features"]);
    }
}
