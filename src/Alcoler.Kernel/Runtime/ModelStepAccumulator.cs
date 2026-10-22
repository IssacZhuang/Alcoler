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
    private readonly List<ReasoningDraft> _reasoningBlocks = [];
    private ReasoningDraft? _openReasoning;
    private readonly List<ToolCall> _toolCalls = [];
    private readonly HashSet<ToolCallId> _toolCallIds = [];
    private ModelFinishReason _finishReason = ModelFinishReason.Stop;

    public ModelStepAccumulator(TurnId turnId)
    {
        _turnId = turnId;
    }

    public IReadOnlyList<ToolCall> ToolCalls => _toolCalls;

    public ToolCall? LastReadyToolCall { get; private set; }

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

            case ModelReasoningStarted:
                OpenReasoningBlock();
                break;

            case ModelReasoningDelta reasoningDelta:
                if (reasoningDelta.Delta.Length > 0)
                {
                    HasBoundaryContent = true;
                }
                OpenReasoningBlock().Text.Append(reasoningDelta.Delta);
                return emit(new ModelStreamingReasoningEvent(_turnId, reasoningDelta.Delta));

            case ModelReasoningEnded reasoningEnded:
                var reasoning = OpenReasoningBlock();
                reasoning.Signature = reasoningEnded.ProviderSignature;
                reasoning.RedactedData = reasoningEnded.RedactedData;
                reasoning.EncryptedContent = reasoningEnded.EncryptedContent;
                reasoning.ProviderItemId = reasoningEnded.ProviderItemId;
                _openReasoning = null;
                break;

            case ModelToolInputDelta toolInputDelta:
                if (toolInputDelta.DeltaJson.Length > 0)
                {
                    HasBoundaryContent = true;
                }
                return emit(new ModelStreamingToolInputEvent(_turnId, toolInputDelta.Id, toolInputDelta.DeltaJson));

            case ModelToolCallReady toolCall:
                HasBoundaryContent = true;
                var trimmed = toolCall.ToolCall with { Name = toolCall.ToolCall.Name.Trim() };
                LastReadyToolCall = trimmed;
                if (_toolCallIds.Add(trimmed.Id))
                    _toolCalls.Add(trimmed);
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
        var parts = BuildCommonParts(_toolCalls.Count);
        foreach (var call in _toolCalls)
        {
            parts.Add(new ToolCallPart(StrongId.NewPartId(), call.Id, call.Name, call.Input));
        }
        return new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant, parts, createdAt);
    }

    public ConversationMessage BuildPartialAssistantMessage(DateTimeOffset createdAt) =>
        new(StrongId.NewMessageId(), MessageRole.Assistant, BuildCommonParts(0), createdAt);

    private List<MessagePart> BuildCommonParts(int extraCapacity)
    {
        var parts = new List<MessagePart>(_reasoningBlocks.Count + 1 + extraCapacity);
        foreach (var block in _reasoningBlocks)
        {
            if (block.Text.Length > 0 || block.Signature is not null
                || block.RedactedData is not null || block.EncryptedContent is not null)
            {
                parts.Add(new ReasoningPart(StrongId.NewPartId(), block.Text.ToString(), block.Signature,
                    block.RedactedData, block.EncryptedContent, block.ProviderItemId));
            }
        }
        if (_text.Length > 0)
        {
            parts.Add(new TextPart(StrongId.NewPartId(), _text.ToString()));
        }
        return parts;
    }

    private ReasoningDraft OpenReasoningBlock()
    {
        if (_openReasoning is null)
            _reasoningBlocks.Add(_openReasoning = new ReasoningDraft());
        return _openReasoning;
    }

    private sealed class ReasoningDraft
    {
        public StringBuilder Text { get; } = new();
        public string? Signature { get; set; }
        public string? RedactedData { get; set; }
        public string? EncryptedContent { get; set; }
        public string? ProviderItemId { get; set; }
    }
}
