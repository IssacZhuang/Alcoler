namespace Alcoler.Kernel.Runtime;

// Mirrors ZCode empty-completion-retry: a stream that finishes without any text, reasoning,
// or tool calls is retried once before the step is treated as finished.
public static class EmptyCompletionRetry
{
    public const int MaxRetries = 1;
}
