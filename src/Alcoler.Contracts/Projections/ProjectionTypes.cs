using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Contracts.Projections;

public enum TurnPhase
{
    Idle,
    ProcessingInput,
    AwaitingModel,
    Streaming,
    ExecutingTools,
    AwaitingPermission,
    AwaitingInteraction,
    Completing,
    Error,
}

public static class ContextUsageSources
{
    public const string SystemToolSchemas = "system_tool_schemas";
    public const string McpToolSchemas = "mcp_tool_schemas";
    public const string SystemPrompt = "system_prompt";
    public const string Skills = "skills";
    public const string MetaUserContext = "meta_user_context";
    public const string ToolPrompt = "tool_prompt";
    public const string Messages = "messages";
}

public sealed record ContextUsageCategory(string Source, long Characters);

public sealed record ContextUsageState(
    int UsedTokens,
    int MaxTokens,
    string TokenSource,
    IReadOnlyList<ContextUsageCategory> Breakdown,
    long CacheReadTokens = 0,
    long CacheInputTokens = 0,
    TokenUsage? LastUsage = null);

public sealed record ConversationState(
    string Title,
    PermissionMode Mode,
    ModelSelection? ModelSelection,
    int ContextWindowTokens,
    TurnPhase Phase,
    TurnId? ActiveTurn,
    int? ContextUsageTokens,
    string TokenSource,
    IReadOnlyList<QueuedInput> Queue,
    PermissionRequest? PendingPermission,
    InteractionRequest? PendingInteraction,
    ContextUsageState? ContextUsage = null)
{
    public static ConversationState Initial(string title, PermissionMode mode, ModelSelection? modelSelection, int contextWindowTokens) =>
        new(title, mode, modelSelection, contextWindowTokens, TurnPhase.Idle, null, null, "estimate", [], null, null);
}

public enum ToolCallStatus
{
    Pending,
    Running,
    AwaitingPermission,
    Succeeded,
    Failed,
    Cancelled,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(UserMessageRow), "user_message")]
[JsonDerivedType(typeof(AssistantMessageRow), "assistant_message")]
[JsonDerivedType(typeof(ToolCallRow), "tool_call")]
[JsonDerivedType(typeof(SystemNoticeRow), "system_notice")]
public abstract record ConversationRow(string RowId);

public sealed record UserMessageRow(
    string RowId,
    string Text,
    DateTimeOffset At) : ConversationRow(RowId);

public sealed record AssistantMessageRow(
    string RowId,
    MessageId Id,
    IReadOnlyList<MessagePart> Parts,
    bool IsStreaming,
    DateTimeOffset At) : ConversationRow(RowId);

public sealed record ToolCallRow(
    string RowId,
    ToolCallId Id,
    string ToolName,
    JsonElement Input,
    ToolCallStatus Status,
    IReadOnlyList<ToolResultBlock>? Output,
    bool IsError,
    TimeSpan? Duration,
    DateTimeOffset StartedAt) : ConversationRow(RowId);

public sealed record SystemNoticeRow(
    string RowId,
    string Kind,
    string Text) : ConversationRow(RowId);

public sealed record ConversationSnapshot(
    long Revision,
    long Sequence,
    IReadOnlyList<ConversationRow> Rows,
    ConversationState State);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RowAppended), "row_appended")]
[JsonDerivedType(typeof(RowUpserted), "row_upserted")]
[JsonDerivedType(typeof(RowRemoved), "row_removed")]
[JsonDerivedType(typeof(StatePatched), "state_patch")]
public abstract record RowOp;

public sealed record RowAppended(ConversationRow Row) : RowOp;

public sealed record RowUpserted(ConversationRow Row) : RowOp;

public sealed record RowRemoved(string RowId) : RowOp;

public sealed record StatePatched(ConversationState State) : RowOp;

public sealed record ConversationDelta(
    long Sequence,
    IReadOnlyList<RowOp> Ops);
