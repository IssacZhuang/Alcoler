using System.Threading.Channels;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;

namespace Alcoler.Tests;

/// <summary>
/// Scriptable projection: an initial snapshot plus a queued delta script. The delegate form
/// lets tests simulate resubscribes (each Subscribe call returns the same instance with its
/// script replayed from the snapshot).
/// </summary>
public sealed class FakeConversationProjection : IConversationProjection
{
    private readonly object _sync = new();
    private readonly Channel<ConversationDelta> _deltas = Channel.CreateUnbounded<ConversationDelta>();
    private ConversationSnapshot _snapshot;

    public FakeConversationProjection(ConversationSnapshot snapshot) => _snapshot = snapshot;

    public int SubscribeCount { get; private set; }

    public void SetSnapshot(ConversationSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot;
        }
    }

    public void Emit(ConversationDelta delta) => _deltas.Writer.TryWrite(delta);

    public void Complete() => _deltas.Writer.TryComplete();

    public ConversationSnapshot Snapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public async IAsyncEnumerable<ConversationDelta> Deltas([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SubscribeCount++;
        await foreach (var delta in _deltas.Reader.ReadAllAsync(cancellationToken))
        {
            yield return delta;
        }
    }

    public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return _snapshot.Sequence >= sequence
                ? Task.CompletedTask
                : Task.FromException(new NotSupportedException("The fake projection does not pump sequences."));
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Minimal runtime recording submitted commands; Subscribe returns scripted projections.</summary>
public sealed class FakeAgentRuntime : IAgentRuntime
{
    private readonly Dictionary<SessionId, FakeConversationProjection> _projections = [];

    public List<ConversationCommand> Submitted { get; } = [];

    public bool RejectNextCommand { get; set; }

    public FakeConversationProjection ProjectionFor(SessionId sessionId) => _projections[sessionId];

    public FakeConversationProjection RegisterSession(SessionId sessionId, ConversationSnapshot snapshot)
    {
        var projection = new FakeConversationProjection(snapshot);
        _projections[sessionId] = projection;
        return projection;
    }

    public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<SessionSummary>>([]);

    public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
    {
        Submitted.Add(command);
        var ack = RejectNextCommand
            ? CommandAck.Rejected(command.CommandId, 0, "rejected for test")
            : CommandAck.AcceptedAt(command.CommandId, 1);
        return ValueTask.FromResult(ack);
    }

    public ValueTask<int> UnloadIdleSessionsAsync(
        TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);

    public IConversationProjection Subscribe(SessionId sessionId) => _projections[sessionId];

    public IConversationProjection? TryObserve(SessionId sessionId) =>
        _projections.TryGetValue(sessionId, out var projection) ? projection : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class TestSnapshots
{
    public static ConversationState State(
        TurnPhase phase = TurnPhase.Idle,
        int? contextUsageTokens = null,
        Alcoler.Contracts.Models.ModelSelection? modelSelection = null) => new(
        "Test session",
        PermissionMode.Build,
        modelSelection,
        200_000,
        phase,
        null,
        contextUsageTokens,
        "estimate",
        [],
        null,
        null);
}
