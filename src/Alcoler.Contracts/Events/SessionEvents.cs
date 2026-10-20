using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionStartedEvent), "session_started")]
[JsonDerivedType(typeof(SessionTitleChangedEvent), "session_title_changed")]
[JsonDerivedType(typeof(SessionModeChangedEvent), "session_mode_changed")]
[JsonDerivedType(typeof(SessionModelChangedEvent), "session_model_changed")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn_started")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn_completed")]
[JsonDerivedType(typeof(TurnFailedEvent), "turn_failed")]
[JsonDerivedType(typeof(UserMessageCommittedEvent), "user_message_committed")]
[JsonDerivedType(typeof(AssistantMessageCommittedEvent), "assistant_message_committed")]
[JsonDerivedType(typeof(ToolCallStartedEvent), "tool_call_started")]
[JsonDerivedType(typeof(ToolCallResultEvent), "tool_call_result")]
[JsonDerivedType(typeof(PermissionRequestedEvent), "permission_requested")]
[JsonDerivedType(typeof(PermissionResolvedEvent), "permission_resolved")]
[JsonDerivedType(typeof(InteractionRequestedEvent), "interaction_requested")]
[JsonDerivedType(typeof(InteractionResolvedEvent), "interaction_resolved")]
[JsonDerivedType(typeof(CompactBoundaryAppliedEvent), "compact_boundary_applied")]
[JsonDerivedType(typeof(MicrocompactAppliedEvent), "microcompact_applied")]
[JsonDerivedType(typeof(InputQueuedEvent), "input_queued")]
[JsonDerivedType(typeof(InputDequeuedEvent), "input_dequeued")]
[JsonDerivedType(typeof(InputDiscardedEvent), "input_discarded")]
[JsonDerivedType(typeof(StreamRecoveryEvent), "stream_recovery")]
[JsonDerivedType(typeof(ContextUsageUpdatedEvent), "context_usage_updated")]
[JsonDerivedType(typeof(ModelRequestStartedEvent), "model_request_started")]
[JsonDerivedType(typeof(ModelRequestCompletedEvent), "model_request_completed")]
[JsonDerivedType(typeof(ModelRequestFailedEvent), "model_request_failed")]
[JsonDerivedType(typeof(ModelRetryScheduledEvent), "model_retry_scheduled")]
[JsonDerivedType(typeof(ModelStreamingTextEvent), "model_streaming_text")]
[JsonDerivedType(typeof(ModelStreamingReasoningEvent), "model_streaming_reasoning")]
[JsonDerivedType(typeof(ModelStreamingToolInputEvent), "model_streaming_tool_input")]
[JsonDerivedType(typeof(SubagentStartedEvent), "subagent_started")]
[JsonDerivedType(typeof(SubagentStatusChangedEvent), "subagent_status_changed")]
public abstract record SessionEvent;

public sealed record SessionStartedEvent(
    string WorkspaceRoot,
    string Title,
    PermissionMode Mode,
    ModelSelection? ModelSelection) : SessionEvent;

public sealed record SessionTitleChangedEvent(string Title) : SessionEvent;

public sealed record SessionModeChangedEvent(PermissionMode Mode) : SessionEvent;

public sealed record SessionModelChangedEvent(ModelSelection Selection, int ContextWindowTokens) : SessionEvent;

public sealed record TurnStartedEvent(TurnId TurnId, CommandId CommandId) : SessionEvent;

public enum TurnOutcome
{
    Completed,
    Cancelled,
    Failed,
    ForcedStop,
}

public sealed record TurnCompletedEvent(TurnId TurnId, TurnOutcome Outcome, TokenUsage? FinalUsage = null) : SessionEvent;

public sealed record TurnFailedEvent(TurnId TurnId, string Error) : SessionEvent;

public sealed record UserMessageCommittedEvent(ConversationMessage Message) : SessionEvent;

public sealed record AssistantMessageCommittedEvent(ConversationMessage Message) : SessionEvent;

public sealed record ToolCallStartedEvent(
    TurnId TurnId,
    ToolCallId ToolCallId,
    string ToolName,
    JsonElement Input) : SessionEvent;

public sealed record ToolCallResultEvent(
    TurnId TurnId,
    ToolCallId ToolCallId,
    string ToolName,
    ToolExecutionResult Result,
    TimeSpan Duration) : SessionEvent;

public sealed record PermissionRequestedEvent(PermissionRequest Request) : SessionEvent;

public sealed record PermissionResolvedEvent(InteractionId InteractionId, PermissionDecision Decision) : SessionEvent;

public sealed record InteractionOption(string Label, string? Description = null);

public sealed record InteractionQuestion(
    string Header,
    string Text,
    IReadOnlyList<InteractionOption> Options,
    bool MultiSelect = false);

public sealed record InteractionRequest(
    InteractionId InteractionId,
    string Prompt,
    IReadOnlyList<InteractionQuestion> Questions);

public sealed record InteractionAnswer(IReadOnlyDictionary<string, string> Answers);

public sealed record InteractionRequestedEvent(InteractionRequest Request) : SessionEvent;

public sealed record InteractionResolvedEvent(InteractionId InteractionId, InteractionAnswer Answer) : SessionEvent;

public sealed record CompactBoundaryAppliedEvent(CompactBoundary Boundary) : SessionEvent;

public sealed record MicrocompactAppliedEvent(IReadOnlyList<PartId> ClearedParts, int EstimatedTokensSaved) : SessionEvent;

public sealed record QueuedInput(CommandId CommandId, string Text, DateTimeOffset QueuedAt);

public sealed record InputQueuedEvent(QueuedInput Input) : SessionEvent;

public sealed record InputDequeuedEvent(CommandId CommandId) : SessionEvent;

public sealed record InputDiscardedEvent(CommandId CommandId, string Reason) : SessionEvent;

public enum StreamRecoveryPhase
{
    AnchorSelected,
    TailDiscarded,
    RetryStarted,
}

public sealed record StreamRecoveryEvent(StreamRecoveryPhase Phase, int RetryAttempt) : SessionEvent;

public sealed record ContextUsageUpdatedEvent(ContextUsageState Usage) : SessionEvent;

public sealed record ModelRequestStartedEvent(TurnId TurnId, int Attempt) : SessionEvent;

public sealed record ModelRequestCompletedEvent(TurnId TurnId, TokenUsage Usage) : SessionEvent;

public sealed record ModelRequestFailedEvent(TurnId TurnId, ModelFailure Failure, int Attempt) : SessionEvent;

public sealed record ModelRetryScheduledEvent(TurnId TurnId, int Attempt, TimeSpan Delay) : SessionEvent;

public sealed record ModelStreamingTextEvent(TurnId TurnId, string Delta) : SessionEvent;

public sealed record ModelStreamingReasoningEvent(TurnId TurnId, string Delta) : SessionEvent;

public sealed record ModelStreamingToolInputEvent(TurnId TurnId, ToolCallId ToolCallId, string DeltaJson) : SessionEvent;
