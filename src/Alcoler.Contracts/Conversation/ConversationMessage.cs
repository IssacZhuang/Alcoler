using System.Text.Json.Serialization;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Conversation;

public enum MessageRole
{
    System,
    User,
    Assistant,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ReasoningPart), "reasoning")]
[JsonDerivedType(typeof(ToolCallPart), "tool_call")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
[JsonDerivedType(typeof(FilePart), "file")]
[JsonDerivedType(typeof(AttachmentPart), "attachment")]
public abstract record MessagePart(PartId Id);

public sealed record TextPart(PartId Id, string Text) : MessagePart(Id);

public sealed record ReasoningPart(PartId Id, string Text, string? ProviderSignature = null) : MessagePart(Id);

public sealed record ToolCallPart(PartId Id, ToolCallId ToolCallId, string ToolName, System.Text.Json.JsonElement Input) : MessagePart(Id);

public sealed record ToolResultPart(PartId Id, ToolCallId ToolCallId, IReadOnlyList<ToolResultBlock> Content, bool IsError = false) : MessagePart(Id);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ToolResultText), "text")]
[JsonDerivedType(typeof(ToolResultMedia), "media")]
public abstract record ToolResultBlock;

public sealed record ToolResultText(string Text) : ToolResultBlock;

public sealed record ToolResultMedia(string ArtifactUri, string MimeType, string? FileName = null) : ToolResultBlock;

public sealed record FilePart(PartId Id, string ArtifactUri, string MimeType, string? FileName = null, bool ProviderReady = true) : MessagePart(Id);

public enum AttachmentKind
{
    ContextPrefix,
    SkillsListing,
    SystemReminder,
    TaskNotification,
    CompactSummary,
    ResumeReferencedContext,
}

public sealed record AttachmentPart(PartId Id, AttachmentKind Kind, string Content, bool ProviderVisible = true) : MessagePart(Id);

public sealed record ConversationMessage(
    MessageId Id,
    MessageRole Role,
    IReadOnlyList<MessagePart> Parts,
    DateTimeOffset CreatedAt);
