using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote;

internal sealed record HistoryBounds(string? Before, string? After);

internal sealed record HistoryRequest(int RequestId, string Direction, string? Cursor)
{
    public const int MaxBytes = 4096;
    public const int QueueCapacity = 8;
    public const int MaxCursorLength = 1024;

    public static HistoryRequest? Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var requestId = 0;
            string? type = null;
            string? direction = null;
            string? cursor = null;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "type" when type is null && property.Value.ValueKind == JsonValueKind.String:
                        type = property.Value.GetString();
                        break;
                    case "requestId" when requestId == 0 && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var id) && id > 0:
                        requestId = id;
                        break;
                    case "before" or "after" when direction is null && property.Value.ValueKind == JsonValueKind.String:
                        direction = property.Name;
                        cursor = property.Value.GetString();
                        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxCursorLength)
                            return null;
                        break;
                    case "latest" when direction is null && property.Value.ValueKind == JsonValueKind.True:
                        direction = "latest";
                        break;
                    default:
                        return null;
                }
            }
            return type == "history" && requestId > 0 && direction is not null
                ? new HistoryRequest(requestId, direction, cursor)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class CompactHistoryBudget
{
    public const int MaxTurns = 20;
    public const int MaxBytes = 128 * 1024;

    public static long RowBytes(ConversationRow row, bool includeReasoning = true) => 512 + StringBytes(row.RowId) + (row switch
    {
        UserMessageRow user => StringBytes(user.Text) + (user.Images?.Sum(image => PartBytes(image, true)) ?? 0),
        AssistantMessageRow assistant => StringBytes(assistant.Id.Value) + assistant.Parts.Sum(part => PartBytes(part, includeReasoning)),
        ToolCallRow tool => 8 * 256 * 6 + 1024 + StringBytes(tool.Id.Value) + StringBytes(tool.ToolName),
        FileChangeSummaryRow files => files.Files.Sum(file => 128 + StringBytes(file.Path)),
        SystemNoticeRow notice => StringBytes(notice.Kind) + StringBytes(notice.Text) + (notice.Subagent is { } agent
            ? 512 + StringBytes(agent.Id.Value) + StringBytes(agent.ChildSessionId.Value) + StringBytes(agent.Description)
                + StringBytes(agent.ResultSummary) + StringBytes(agent.OutputFile) + StringBytes(agent.ParentToolCallId?.Value)
                + StringBytes(agent.AgentType)
            : 0),
        _ => 0L,
    });

    private static long PartBytes(MessagePart part, bool includeReasoning) => part switch
    {
        TextPart text => 128 + StringBytes(text.Id.Value) + StringBytes(text.Text),
        ReasoningPart reasoning when includeReasoning => 256 + StringBytes(reasoning.Id.Value) + StringBytes(reasoning.Text)
            + StringBytes(reasoning.ProviderSignature) + StringBytes(reasoning.RedactedData)
            + StringBytes(reasoning.EncryptedContent) + StringBytes(reasoning.ProviderItemId),
        FilePart file => 256 + StringBytes(file.Id.Value) + StringBytes(file.ArtifactUri) + StringBytes(file.MimeType) + StringBytes(file.FileName),
        _ => 0,
    };

    private static long StringBytes(string? text)
    {
        if (text is null)
            return 4;
        if (text.Length >= MaxBytes)
            return MaxBytes;
        long length = text.Length + 2;
        foreach (var character in text)
        {
            if (character is < ' ' or > '~' or '"' or '\'' or '&' or '+' or '<' or '>' or '`') length += 5;
            else if (character == '\\') length++;
        }
        return length;
    }
}
