using System.Runtime.CompilerServices;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Deterministic test model for multi-step turns: each <see cref="StreamAsync"/> call serves
/// exactly one script step, in order. A step that ends in <see cref="ModelStreamFailed"/> does
/// not advance the cursor, so a retry re-requests the same step; FailOnce steps degrade to
/// their events-only form from that step's second attempt onward. Serving past the last step
/// throws, so under-scripted tests fail loudly instead of looping.
/// </summary>
public sealed class SequencedChatModel(IReadOnlyList<ScriptStep> script, ModelDescriptor? descriptor = null)
    : IChatModel
{
    private readonly object _cursorSync = new();
    private int _index;
    private int _stepAttempts;

    public ModelDescriptor Descriptor { get; } = descriptor ?? new ModelDescriptor(
        "scripted",
        "scripted-model",
        "Scripted Model",
        new ModelProperties(ContextWindowTokens: 200_000, DefaultMaxOutputTokens: 32_768));

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        ScriptStep step;
        int attempt;
        lock (_cursorSync)
        {
            if (_index >= script.Count)
            {
                throw new InvalidOperationException(
                    $"script exhausted after {script.Count} step(s); the turn requested another model call");
            }
            step = script[_index];
            attempt = ++_stepAttempts;
        }

        var failed = false;
        switch (step)
        {
            case ScriptStep.Events events:
                foreach (var streamEvent in events.Items)
                    yield return streamEvent;
                break;

            case ScriptStep.Fail fail:
                failed = true;
                yield return new ModelStreamFailed(fail.Failure);
                break;

            case ScriptStep.FailOnce failOnce:
                foreach (var streamEvent in failOnce.After)
                    yield return streamEvent;
                if (attempt == 1)
                {
                    failed = true;
                    yield return new ModelStreamFailed(failOnce.Failure);
                }
                break;
        }

        if (!failed)
        {
            lock (_cursorSync)
            {
                _index++;
                _stepAttempts = 0;
            }
        }
    }
}
