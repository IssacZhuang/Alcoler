using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Runtime;

public sealed class ModelStepAccumulator
{
    private readonly TurnId _turnId;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly List<ToolCall> _toolCalls = [];
    private string? _reasoningSignature;
    private ModelFinishReason _finishReason = ModelFinishReason.Stop;

    public ModelStepAccumulator(TurnId turnId)
    {
        _turnId = turnId;
    }

    public IReadOnlyList<ToolCall> ToolCalls => _toolCalls;

    public TokenUsage? Usage { get; private set; }

    public ModelFailure? Failure { get; private set; }

    public bool HasBoundaryContent { get; private set; }

    public ModelFinishReason FinishReason => _finishReason;

    public ValueTask ApplyAsync(ModelStreamEvent streamEvent, Func<SessionEvent, ValueTask> emit)
    {
        switch (streamEvent)
        {
            case ModelTextDelta textDelta:
                if (textDelta.Delta.Length > 0)
                {
                    HasBoundaryContent = true;
                }
                _text.Append(textDelta.Delta);
                return emit(new ModelStreamingTextEvent(_turnId, textDelta.Delta));

            case ModelReasoningDelta reasoningDelta:
                if (reasoningDelta.Delta.Length > 0)
                {
                    HasBoundaryContent = true;
                }
                _reasoning.Append(reasoningDelta.Delta);
                return emit(new ModelStreamingReasoningEvent(_turnId, reasoningDelta.Delta));

            case ModelReasoningEnded reasoningEnded:
                _reasoningSignature = reasoningEnded.ProviderSignature;
                break;

            case ModelToolInputDelta toolInputDelta:
                if (toolInputDelta.DeltaJson.Length > 0)
                {
                    HasBoundaryContent = true;
                }
                return emit(new ModelStreamingToolInputEvent(_turnId, toolInputDelta.Id, toolInputDelta.DeltaJson));

            case ModelToolCallReady toolCall:
                HasBoundaryContent = true;
                _toolCalls.Add(toolCall.ToolCall);
                break;

            case ModelStreamFinished finished:
                Usage = finished.Usage;
                _finishReason = finished.FinishReason;
                break;

            case ModelStreamFailed failed:
                Failure = failed.Failure;
                break;
        }

        return ValueTask.CompletedTask;
    }

    public ConversationMessage BuildAssistantMessage(DateTimeOffset createdAt)
    {
        var parts = BuildCommonParts();
        parts.AddRange(_toolCalls.Select(call => new ToolCallPart(
            StrongId.NewPartId(),
            call.Id,
            call.Name,
            call.Input)));
        return new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant, parts, createdAt);
    }

    public ConversationMessage BuildPartialAssistantMessage(DateTimeOffset createdAt) =>
        new(StrongId.NewMessageId(), MessageRole.Assistant, BuildCommonParts(), createdAt);

    private List<MessagePart> BuildCommonParts()
    {
        var parts = new List<MessagePart>();
        if (_reasoning.Length > 0)
        {
            parts.Add(new ReasoningPart(StrongId.NewPartId(), _reasoning.ToString(), _reasoningSignature));
        }
        if (_text.Length > 0)
        {
            parts.Add(new TextPart(StrongId.NewPartId(), _text.ToString()));
        }
        return parts;
    }
}
