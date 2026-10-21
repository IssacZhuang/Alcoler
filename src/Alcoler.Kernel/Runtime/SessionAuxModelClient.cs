using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Models;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Runs auxiliary one-shot completions on the session model with the lowest reasoning
/// level and no tools, mirroring ZCode's auxiliary-model budget (cap 5000 output tokens).
/// </summary>
internal sealed class SessionAuxModelClient(Func<ModelSelection?> resolveSelection, Func<ModelSelection, IChatModel> resolveModel) : IAuxModelClient
{
    private const int MaxOutputTokensCap = 5_000;

    public ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken) =>
        CompleteAsync(request, cancellationToken, separateSystemPrompt: false);

    internal async ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken, bool separateSystemPrompt)
    {
        var selection = resolveSelection()
            ?? throw new InvalidOperationException("No model configured for the session.");
        var model = resolveModel(selection);
        var baseOptions = selection.Options ?? new ModelCallOptions();
        var maxOutput = Math.Min(
            Math.Min(request.MaxOutputTokens, MaxOutputTokensCap),
            model.Descriptor.Properties.DefaultMaxOutputTokens ?? int.MaxValue);
        var options = new ModelCallOptions(
            ReasoningLevel: model.Descriptor.Properties.ReasoningLevels is { Count: > 0 } levels ? levels[0] : baseOptions.ReasoningLevel,
            MaxOutputTokens: Math.Max(1, maxOutput));

        ModelInputMessage[] messages = separateSystemPrompt
            ? [
                new(MessageRole.System, [new InputTextBlock(request.SystemPrompt)]),
                new(MessageRole.User, [new InputTextBlock(request.UserPrompt)]),
            ]
            : [
                new(MessageRole.User,
                [
                    new InputTextBlock(string.IsNullOrEmpty(request.SystemPrompt)
                        ? request.UserPrompt
                        : $"{request.SystemPrompt}\n\n{request.UserPrompt}"),
                ]),
            ];
        var modelRequest = new ModelRequest(messages, [], options);

        var text = new StringBuilder();
        await foreach (var streamEvent in ModelStreamRunner.RunAsync(
                           model,
                           modelRequest,
                           new ModelRunnerOptions(),
                           cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is ModelTextDelta delta)
            {
                text.Append(delta.Delta);
            }
            else if (streamEvent is ModelToolCallReady && separateSystemPrompt)
            {
                throw new InvalidOperationException("The auxiliary title request returned a tool call.");
            }
            else if (streamEvent is ModelStreamFailed)
            {
                throw new InvalidOperationException("The auxiliary model request failed.");
            }
        }

        return text.ToString();
    }
}
