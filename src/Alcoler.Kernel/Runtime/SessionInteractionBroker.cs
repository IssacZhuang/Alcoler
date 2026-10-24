using System.Collections.Concurrent;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;

namespace Alcoler.Kernel.Runtime;

public sealed class SessionInteractionBroker : IPermissionBroker, IUserInteractionPort
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<InteractionId, TaskCompletionSource<PermissionDecision>> _pendingPermissions = new();
    private readonly ConcurrentDictionary<InteractionId, TaskCompletionSource<InteractionAnswer>> _pendingInteractions = new();
    private readonly Func<SessionEvent, ValueTask> _emit;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;

    public SessionInteractionBroker(Func<SessionEvent, ValueTask> emit, TimeProvider clock, TimeSpan? timeout = null)
    {
        _emit = emit;
        _clock = clock;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPermissions[request.InteractionId] = completion;
        var requested = false;
        try
        {
            await _emit(new PermissionRequestedEvent(request)).ConfigureAwait(false);
            requested = true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = _clock.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                timeoutCts,
                _timeout,
                Timeout.InfiniteTimeSpan);
            await using var registration = timeoutCts.Token.Register(static s =>
                ((TaskCompletionSource<PermissionDecision>)s!).TrySetResult(PermissionDecision.Deny), completion);
            return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PermissionDecision.Deny;
        }
        finally
        {
            // Removal happens before the emitted decision is read so a concurrent resolver
            // cannot re-enter the pending entry, and only this terminal path emits the event.
            _pendingPermissions.TryRemove(request.InteractionId, out _);
            if (requested)
            {
                var decision = completion.Task.IsCompletedSuccessfully
                    ? completion.Task.Result
                    : PermissionDecision.Deny;
                try
                {
                    await _emit(new PermissionResolvedEvent(request.InteractionId, decision)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Session disposal may cancel the durable append; the resolved event is best-effort on shutdown.
                }
            }
        }
    }

    public async ValueTask<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<InteractionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInteractions[request.InteractionId] = completion;
        var requested = false;
        try
        {
            await _emit(new InteractionRequestedEvent(request)).ConfigureAwait(false);
            requested = true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = _clock.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                timeoutCts,
                _timeout,
                Timeout.InfiniteTimeSpan);
            await using var registration = timeoutCts.Token.Register(static s =>
                ((TaskCompletionSource<InteractionAnswer>)s!).TrySetResult(new InteractionAnswer(
                    new Dictionary<string, string>())), completion);
            return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new InteractionAnswer(new Dictionary<string, string>());
        }
        finally
        {
            _pendingInteractions.TryRemove(request.InteractionId, out _);
            if (requested)
            {
                var answer = completion.Task.IsCompletedSuccessfully
                    ? completion.Task.Result
                    : new InteractionAnswer(new Dictionary<string, string>());
                try
                {
                    await _emit(new InteractionResolvedEvent(request.InteractionId, answer)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Session disposal may cancel the durable append; the resolved event is best-effort on shutdown.
                }
            }
        }
    }

    public bool ResolvePermission(InteractionId interactionId, PermissionDecision decision)
    {
        if (decision.Outcome == PermissionOutcome.Modified && decision.ModifiedInput is null)
        {
            decision = PermissionDecision.Deny;
        }
        return _pendingPermissions.TryGetValue(interactionId, out var completion)
            && completion.TrySetResult(decision);
    }

    public bool ResolveInteraction(InteractionId interactionId, InteractionAnswer answer) =>
        _pendingInteractions.TryGetValue(interactionId, out var completion)
        && completion.TrySetResult(answer);
}
