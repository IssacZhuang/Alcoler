using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Models;

public enum CacheControlKind
{
    Ephemeral,
}

public sealed record CacheControl(CacheControlKind Kind = CacheControlKind.Ephemeral);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(InputTextBlock), "text")]
[JsonDerivedType(typeof(InputReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(InputImageBlock), "image")]
[JsonDerivedType(typeof(InputToolCallBlock), "tool_call")]
[JsonDerivedType(typeof(InputToolResultBlock), "tool_result")]
public abstract record ModelContentBlock;

public sealed record InputTextBlock(string Text) : ModelContentBlock;

public sealed record InputReasoningBlock(
    string Text,
    string? Signature = null,
    string? RedactedData = null,
    string? EncryptedContent = null,
    string? ProviderItemId = null) : ModelContentBlock;

public sealed record InputImageBlock(string MimeType, string Base64Data) : ModelContentBlock;

public sealed record InputToolCallBlock(ToolCallId Id, string Name, JsonElement Input) : ModelContentBlock;

public sealed record InputToolResultBlock(
    ToolCallId Id,
    string Content,
    bool IsError = false,
    IReadOnlyList<InputImageBlock>? Images = null) : ModelContentBlock;

public sealed record ModelInputMessage(
    MessageRole Role,
    IReadOnlyList<ModelContentBlock> Content,
    CacheControl? CacheControl = null,
    string? ProviderId = null,
    string? ModelId = null);

/// <summary>
/// Session identity for providers that route prefix caching per conversation; the
/// parent id marks subagent sessions so sibling requests can share a cache route.
/// </summary>
public sealed record ModelCacheAffinity(string SessionId, string? ParentSessionId = null);

public sealed record ModelRequest(
    IReadOnlyList<ModelInputMessage> Messages,
    IReadOnlyList<ToolContract> Tools,
    ModelCallOptions Options,
    ModelCacheAffinity? CacheAffinity = null);
