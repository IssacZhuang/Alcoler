namespace Alcoler.Kernel.Runtime;

// Mirrors ZCode turn-output-token-continuation: when a response is cut by the output token
// limit, a user-role Continue prompt asks the model to resume mid-thought, at most
// MaxContinuationsPerTurn times per turn.
public static class OutputContinuation
{
    public const int MaxContinuationsPerTurn = 3;

    public const string ContinuePromptText =
        "Output token limit hit. Resume directly — no apology, no recap of what you were doing. " +
        "Pick up mid-thought if that is where the cut happened. Break remaining work into smaller pieces.";

    public const string LimitExceededMessage =
        "The model's response exceeded the output token maximum.";
}
