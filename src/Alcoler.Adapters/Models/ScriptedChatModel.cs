using System.Runtime.CompilerServices;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

public abstract record ScriptStep
{
    /// <summary>Emits the given events on every attempt.</summary>
    public sealed record Events(params ModelStreamEvent[] Items) : ScriptStep;

    /// <summary>Ends every attempt with <see cref="ModelStreamFailed"/> carrying the failure.</summary>
    public sealed record Fail(ModelFailure Failure) : ScriptStep;

    /// <summary>
    /// Emits <paramref name="After"/> events on every attempt; only the first attempt
    /// additionally ends with <see cref="ModelStreamFailed"/>.
    /// </summary>
    public sealed record FailOnce(ModelFailure Failure, params ModelStreamEvent[] After) : ScriptStep;
}

/// <summary>
/// Deterministic test model replaying a script per attempt. Steps run in order;
/// <see cref="ScriptStep.FailOnce"/> degrades to its events-only form from the second
/// attempt onward so retry scenarios stay scripted in a single step list.
/// </summary>
public sealed class ScriptedChatModel(IReadOnlyList<ScriptStep> script, ModelDescriptor? descriptor = null)
    : IChatModel
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

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
        var attempt = Interlocked.Increment(ref _attempts);
        foreach (var step in script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case ScriptStep.Events events:
                    foreach (var streamEvent in events.Items)
                        yield return streamEvent;
                    break;

                case ScriptStep.Fail fail:
                    yield return new ModelStreamFailed(fail.Failure);
                    yield break;

                case ScriptStep.FailOnce failOnce:
                    foreach (var streamEvent in failOnce.After)
                        yield return streamEvent;
                    if (attempt == 1)
                    {
                        yield return new ModelStreamFailed(failOnce.Failure);
                        yield break;
                    }
                    break;
            }
        }
    }
}
