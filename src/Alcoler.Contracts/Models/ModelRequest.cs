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
[JsonDerivedType(typeof(InputToolCallBlock), "tool_call")]
[JsonDerivedType(typeof(InputToolResultBlock), "tool_result")]
public abstract record ModelContentBlock;

public sealed record InputTextBlock(string Text) : ModelContentBlock;

public sealed record InputToolCallBlock(ToolCallId Id, string Name, JsonElement Input) : ModelContentBlock;

public sealed record InputToolResultBlock(ToolCallId Id, string Content, bool IsError = false) : ModelContentBlock;

public sealed record ModelInputMessage(
    MessageRole Role,
    IReadOnlyList<ModelContentBlock> Content,
    CacheControl? CacheControl = null);

public sealed record ModelRequest(
    IReadOnlyList<ModelInputMessage> Messages,
    IReadOnlyList<ToolContract> Tools,
    ModelCallOptions Options);
