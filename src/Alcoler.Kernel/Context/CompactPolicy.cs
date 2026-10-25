namespace Alcoler.Kernel.Context;

public sealed record ShouldAutoCompactInput(
    int EffectiveTokens,
    int ContextWindowTokens,
    int AssistantLedRoundCount,
    int ConsecutiveFailures,
    int ConsecutiveRapidRefills,
    bool Disabled = false);

public sealed record ShouldAutoCompactResult(bool Compact, string Reason);

public static class CompactPolicy
{
    public const int DefaultContextWindowTokens = 200_000;
    public const int AutoCompactOutputReserveTokens = 21_000;
    public const int AutoCompactRequestedReserveTokens = 32_000;
    public const int AutoCompactBufferTokens = 13_000;
    public const int MaxConsecutiveAutoCompactFailures = 3;
    public const int RapidRefillToolTurnThreshold = 3;
    public const int MaxConsecutiveRapidRefills = 3;
    public const int MicroCompactIdleMinutes = 60;
    public const int MicroCompactKeepRecentGroups = 5;
    public const int MicroCompactMinSavingsTokens = 256;
    public const int SummaryMaxOutputTokens = 20_000;
    public const int CompactToolKeepMaxCount = 100;
    public const int AutoCompactMaxAttempts = 3;
    public const int PromptTooLongMaxRetries = 3;

    public static int EffectiveContextWindow(int contextWindowTokens) =>
        Math.Max(0, contextWindowTokens - Math.Min(AutoCompactOutputReserveTokens, contextWindowTokens));

    public static int AutoCompactThreshold(int contextWindowTokens) =>
        Math.Max(0, EffectiveContextWindow(contextWindowTokens) - AutoCompactBufferTokens);

    public static int MicroCompactTriggerTokens(int autoCompactThreshold) =>
        Math.Max(0, Math.Min(autoCompactThreshold * 9 / 10, autoCompactThreshold - 2_000));

    public static string RapidRefillBreakerMessage(int toolTurnThreshold, int maxConsecutiveRapidRefills) =>
        $"Autocompact stopped because the context refilled within fewer than {toolTurnThreshold} tool turns after compaction {maxConsecutiveRapidRefills} times in a row. A file or tool output may be too large. Read it in smaller chunks, or start a new session.";

    public static ShouldAutoCompactResult ShouldAutoCompact(ShouldAutoCompactInput input)
    {
        if (input.Disabled)
            return new ShouldAutoCompactResult(false, "disabled");
        if (input.AssistantLedRoundCount < 2)
            return new ShouldAutoCompactResult(false, "not_enough_messages");
        if (input.ConsecutiveFailures >= MaxConsecutiveAutoCompactFailures
            || input.ConsecutiveRapidRefills >= MaxConsecutiveRapidRefills)
            return new ShouldAutoCompactResult(false, "circuit_breaker");

        return input.EffectiveTokens < AutoCompactThreshold(input.ContextWindowTokens)
            ? new ShouldAutoCompactResult(false, "below_threshold")
            : new ShouldAutoCompactResult(true, "above_threshold");
    }
}
