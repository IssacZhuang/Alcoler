using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.Permissions;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionInteractionBrokerTests
{
    [Fact]
    public async Task DecideAsync_UserResolve_EmitsOneRequestedAndOneResolvedEvent()
    {
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreatePermissionRequest();

        var pending = broker.DecideAsync(request, CancellationToken.None).AsTask();
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        Assert.True(broker.ResolvePermission(request.InteractionId, PermissionDecision.Allow));

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<PermissionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Equal(PermissionOutcome.Allowed, resolved.Decision.Outcome);
    }

    [Fact]
    public async Task DecideAsync_Timeout_EmitsOneDenyResolvedEvent()
    {
        var clock = new ManualTimeProvider();
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, clock, TimeSpan.FromMilliseconds(50));
        var request = CreatePermissionRequest();

        var pending = broker.DecideAsync(request, CancellationToken.None).AsTask();
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<PermissionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Equal(PermissionOutcome.Denied, resolved.Decision.Outcome);

        Assert.False(broker.ResolvePermission(request.InteractionId, PermissionDecision.Allow));
        Assert.Single(sink.Events.OfType<PermissionResolvedEvent>());
    }

    [Fact]
    public async Task DecideAsync_Cancellation_EmitsOneDenyResolvedEvent()
    {
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreatePermissionRequest();
        using var cancellation = new CancellationTokenSource();

        var pending = broker.DecideAsync(request, cancellation.Token).AsTask();
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        cancellation.Cancel();

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Single(sink.Events.OfType<PermissionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<PermissionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Equal(PermissionOutcome.Denied, resolved.Decision.Outcome);
    }

    [Fact]
    public async Task AskAsync_UserAnswer_EmitsOneRequestedAndOneResolvedEvent()
    {
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreateInteractionRequest();
        var answer = new InteractionAnswer(new Dictionary<string, string> { ["Library"] = "NodaTime" });

        var pending = broker.AskAsync(request, CancellationToken.None).AsTask();
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        Assert.True(broker.ResolveInteraction(request.InteractionId, answer));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("NodaTime", result.Answers["Library"]);
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<InteractionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Equal("NodaTime", resolved.Answer.Answers["Library"]);
    }

    [Fact]
    public async Task AskAsync_Timeout_EmitsOneEmptyResolvedEvent()
    {
        var clock = new ManualTimeProvider();
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, clock, TimeSpan.FromMilliseconds(50));
        var request = CreateInteractionRequest();

        var pending = broker.AskAsync(request, CancellationToken.None).AsTask();
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(result.Answers);
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<InteractionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Empty(resolved.Answer.Answers);

        Assert.False(broker.ResolveInteraction(request.InteractionId,
            new InteractionAnswer(new Dictionary<string, string> { ["Library"] = "NodaTime" })));
        Assert.Single(sink.Events.OfType<InteractionResolvedEvent>());
    }

    [Fact]
    public async Task AskAsync_Cancellation_EmitsOneEmptyResolvedEvent()
    {
        var sink = new EventSink();
        var broker = new SessionInteractionBroker(sink.Emit, TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreateInteractionRequest();
        using var cancellation = new CancellationTokenSource();

        var pending = broker.AskAsync(request, cancellation.Token).AsTask();
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        cancellation.Cancel();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(result.Answers);
        Assert.Single(sink.Events.OfType<InteractionRequestedEvent>());
        var resolved = Assert.Single(sink.Events.OfType<InteractionResolvedEvent>());
        Assert.Equal(request.InteractionId, resolved.InteractionId);
        Assert.Empty(resolved.Answer.Answers);
    }

    private static PermissionRequest CreatePermissionRequest() => new(
        StrongId.NewInteractionId(),
        StrongId.NewSessionId(),
        "Write",
        JsonSerializer.SerializeToElement(new { file_path = "notes.txt" }),
        RiskLevel.Low,
        "workspace-modifying tool requires approval in build mode");

    private static InteractionRequest CreateInteractionRequest() => new(
        StrongId.NewInteractionId(),
        "AskUserQuestion pauses execution to collect answers from the user",
        [
            new InteractionQuestion(
                "Library",
                "Which library should we use?",
                [new InteractionOption("NodaTime"), new InteractionOption("DateTime")]),
        ]);

    private sealed class EventSink
    {
        private readonly object _gate = new();
        private readonly List<SessionEvent> _events = [];

        public IReadOnlyList<SessionEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask Emit(SessionEvent sessionEvent)
        {
            lock (_gate)
            {
                _events.Add(sessionEvent);
            }
            return ValueTask.CompletedTask;
        }
    }
}
