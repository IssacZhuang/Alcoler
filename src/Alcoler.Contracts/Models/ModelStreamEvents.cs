using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Models;

public sealed record ToolCall(ToolCallId Id, string Name, JsonElement Input);

public enum ModelFinishReason
{
    Stop,
    ToolCalls,
    Length,
    ContentFilter,
    Other,
}

public sealed record TokenUsage(
    int? InputTokens = null,
    int? OutputTokens = null,
    int? ContextUsageTokens = null,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null);

public enum ModelFailureKind
{
    ProviderError,
    NetworkError,
    RateLimited,
    ContextOverflow,
    InvalidRequest,
    AuthenticationFailed,
    Timeout,
    StreamProtocolError,
    Cancelled,
}

public enum ModelFailurePhase
{
    RequestSetup,
    ResponseBody,
}

public sealed record ModelFailure(
    ModelFailureKind Kind,
    string Message,
    string? ProviderCode = null,
    TimeSpan? RetryAfter = null,
    int? DeclaredTokenGap = null,
    ModelFailurePhase? Phase = null,
    int? HttpStatusCode = null)
{
    public bool IsRetryable => Kind is ModelFailureKind.NetworkError
        or ModelFailureKind.RateLimited
        or ModelFailureKind.ProviderError
        or ModelFailureKind.Timeout;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ModelStreamStarted), "start")]
[JsonDerivedType(typeof(ModelTextStarted), "text_start")]
[JsonDerivedType(typeof(ModelTextDelta), "text_delta")]
[JsonDerivedType(typeof(ModelTextEnded), "text_end")]
[JsonDerivedType(typeof(ModelReasoningStarted), "reasoning_start")]
[JsonDerivedType(typeof(ModelReasoningDelta), "reasoning_delta")]
[JsonDerivedType(typeof(ModelReasoningEnded), "reasoning_end")]
[JsonDerivedType(typeof(ModelToolInputStarted), "tool_input_start")]
[JsonDerivedType(typeof(ModelToolInputDelta), "tool_input_delta")]
[JsonDerivedType(typeof(ModelToolInputEnded), "tool_input_end")]
[JsonDerivedType(typeof(ModelToolCallReady), "tool_call")]
[JsonDerivedType(typeof(ModelStreamFinished), "finish")]
[JsonDerivedType(typeof(ModelStreamFailed), "error")]
public abstract record ModelStreamEvent;

public sealed record ModelStreamStarted : ModelStreamEvent;
public sealed record ModelTextStarted : ModelStreamEvent;
public sealed record ModelTextDelta(string Delta) : ModelStreamEvent;
public sealed record ModelTextEnded : ModelStreamEvent;
public sealed record ModelReasoningStarted : ModelStreamEvent;
public sealed record ModelReasoningDelta(string Delta) : ModelStreamEvent;
public sealed record ModelReasoningEnded(
    string? ProviderSignature = null,
    string? RedactedData = null,
    string? EncryptedContent = null,
    string? ProviderItemId = null) : ModelStreamEvent;
public sealed record ModelToolInputStarted(ToolCallId Id, string ToolName) : ModelStreamEvent;
public sealed record ModelToolInputDelta(ToolCallId Id, string DeltaJson) : ModelStreamEvent;
public sealed record ModelToolInputEnded(ToolCallId Id) : ModelStreamEvent;
public sealed record ModelToolCallReady(ToolCall ToolCall) : ModelStreamEvent;
public sealed record ModelStreamFinished(
    ModelFinishReason FinishReason,
    TokenUsage? Usage = null,
    string? RawFinishReason = null,
    bool ContextWindowExceeded = false) : ModelStreamEvent;
public sealed record ModelStreamFailed(ModelFailure Failure) : ModelStreamEvent;
