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
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public async Task Composer_TurnActive_DisablesSendAndShowsStop()
    {
        using var harness = Show<ComposerView>();
        await PumpAsync();
        var send = FindNamed<Button>(harness.View, "SendButton");
        var stop = FindNamed<Button>(harness.View, "StopButton");
        Assert.False(stop.IsVisible);

        harness.ViewModel.Composer.Draft = "queued thought";
        Assert.True(send.IsEnabled);

        harness.ViewModel.Composer.IsTurnActive = true;
        Assert.False(send.IsEnabled);
        Assert.True(stop.IsVisible);
    }
}
