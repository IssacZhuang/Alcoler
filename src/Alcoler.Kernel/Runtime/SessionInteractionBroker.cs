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

    public SessionInteractionBroker(Func<SessionEvent, ValueTask> emit, TimeProvider clock)
    {
        _emit = emit;
        _clock = clock;
    }

    public async ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPermissions[request.InteractionId] = completion;
        try
        {
            await _emit(new PermissionRequestedEvent(request));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);
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
            _pendingPermissions.TryRemove(request.InteractionId, out _);
        }
    }

    public async ValueTask<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<InteractionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInteractions[request.InteractionId] = completion;
        try
        {
            await _emit(new InteractionRequestedEvent(request));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);
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
