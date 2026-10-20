using Alcoler.Contracts.Projections;

namespace Alcoler.Kernel.Runtime;

public static class TurnStateMachine
{
    private static readonly IReadOnlyDictionary<TurnPhase, TurnPhase[]> Transitions = new Dictionary<TurnPhase, TurnPhase[]>
    {
        [TurnPhase.Idle] = [TurnPhase.ProcessingInput],
        [TurnPhase.ProcessingInput] = [TurnPhase.AwaitingModel, TurnPhase.Streaming, TurnPhase.Completing, TurnPhase.Error],
        [TurnPhase.AwaitingModel] = [TurnPhase.Streaming, TurnPhase.ExecutingTools, TurnPhase.Completing, TurnPhase.AwaitingPermission, TurnPhase.AwaitingInteraction, TurnPhase.Error],
        [TurnPhase.Streaming] = [TurnPhase.ExecutingTools, TurnPhase.Completing, TurnPhase.AwaitingModel, TurnPhase.AwaitingPermission, TurnPhase.AwaitingInteraction, TurnPhase.Error],
        [TurnPhase.ExecutingTools] = [TurnPhase.AwaitingModel, TurnPhase.Completing, TurnPhase.AwaitingPermission, TurnPhase.AwaitingInteraction, TurnPhase.Error],
        [TurnPhase.AwaitingPermission] = [TurnPhase.ExecutingTools, TurnPhase.Completing, TurnPhase.Error],
        [TurnPhase.AwaitingInteraction] = [TurnPhase.ExecutingTools, TurnPhase.AwaitingModel, TurnPhase.Completing, TurnPhase.Error],
        [TurnPhase.Completing] = [TurnPhase.Idle],
        [TurnPhase.Error] = [TurnPhase.Idle],
    };

    public static bool CanTransition(TurnPhase from, TurnPhase to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void Validate(TurnPhase from, TurnPhase to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal turn phase transition {from} -> {to}.");
        }
    }
}

public sealed class TurnMachine
{
    private readonly object _sync = new();

    public TurnPhase Phase { get { lock (_sync) return _phase; } }

    private TurnPhase _phase = TurnPhase.Idle;

    public bool TryTransition(TurnPhase to)
    {
        lock (_sync)
        {
            if (!TurnStateMachine.CanTransition(_phase, to))
            {
                return false;
            }
            _phase = to;
            return true;
        }
    }

    public void Transition(TurnPhase to)
    {
        lock (_sync)
        {
            TurnStateMachine.Validate(_phase, to);
            _phase = to;
        }
    }

    public bool IsTerminal => Phase is TurnPhase.Completing or TurnPhase.Error;

    public bool IsBusy => Phase is not TurnPhase.Idle;
}
