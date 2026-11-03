using System.Globalization;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote;

internal static class CompactJson
{
    private const int MaxHeaderFields = 8;
    private const int MaxHeaderLength = 256;
    private static readonly string[] HeaderFields =
    [
        "subagent_type", "description", "path", "file_path", "filePath", "command", "pattern", "query", "url",
        "action", "agentId", "taskId",
    ];

    public static byte[] SnapshotFrame(
        long revision, long sequence, IEnumerable<CompactWireRow> rows, ConversationState state, HistoryBounds? history = null) =>
        GatewayJson.Object(writer =>
        {
            writer.WriteString("type", "snapshot");
            writer.WriteStartObject("snapshot");
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("sequence", sequence);
            writer.WriteStartArray("rows");
            foreach (var row in rows) WriteRow(writer, row);
            writer.WriteEndArray();
            writer.WritePropertyName("state");
            JsonSerializer.Serialize(writer, state, GatewayJson.Options);
            if (history is not null) WriteHistory(writer, history);
            writer.WriteEndObject();
        });

    public static byte[] DeltaFrame(long sequence, IReadOnlyList<CompactOperation> operations, HistoryBounds? history = null) =>
        GatewayJson.Object(writer =>
        {
            writer.WriteString("type", "delta");
            writer.WriteStartObject("delta");
            writer.WriteNumber("sequence", sequence);
            writer.WriteStartArray("ops");
            foreach (var operation in operations)
            {
                if (operation.Original is { } original)
                {
                    JsonSerializer.Serialize(writer, original, GatewayJson.Options);
                    continue;
                }
                writer.WriteStartObject();
                writer.WriteString("$type", operation.Type);
                writer.WritePropertyName("row");
                WriteRow(writer, operation.Row!);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (history is not null) WriteHistory(writer, history);
            writer.WriteEndObject();
        });

    public static byte[] HistoryFrame(
        int requestId, long sequence, IEnumerable<CompactWireRow> rows, HistoryBounds history, string direction) =>
        GatewayJson.Object(writer =>
        {
            writer.WriteString("type", "history");
            writer.WriteNumber("requestId", requestId);
            writer.WriteStartObject("page");
            writer.WriteStartArray("rows");
            foreach (var row in rows) WriteRow(writer, row);
            writer.WriteEndArray();
            writer.WriteNumber("sequence", sequence);
            WriteHistory(writer, history);
            writer.WriteString("direction", direction);
            writer.WriteEndObject();
        });

    public static byte[] StaleHistory(int requestId, long sequence, HistoryBounds history) => GatewayJson.Object(writer =>
    {
        writer.WriteString("type", "history");
        writer.WriteNumber("requestId", requestId);
        writer.WriteString("error", "stale_history");
        writer.WriteNumber("sequence", sequence);
        WriteHistory(writer, history);
    });

    private static void WriteHistory(Utf8JsonWriter writer, HistoryBounds history)
    {
        writer.WriteStartObject("history");
        writer.WriteString("before", history.Before);
        writer.WriteString("after", history.After);
        writer.WriteEndObject();
    }

    public static byte[] TurnDetails(CompactTurnSummary summary, long sequence) => GatewayJson.Object(writer =>
    {
        writer.WriteStartArray("rows");
        for (var index = 1; index < summary.FinalIndex; index++)
            WriteRow(writer, new CompactWireRow(summary.Turn.Rows[index]));
        if (summary.Answer.Parts.Any(part => part is ReasoningPart))
        {
            ConversationRow reasoning = summary.Answer with
            {
                Parts = summary.Answer.Parts.Where(part => part is ReasoningPart).ToArray(),
            };
            JsonSerializer.Serialize(writer, reasoning, GatewayJson.Options);
        }
        writer.WriteEndArray();
        writer.WriteString("version", summary.Version);
        writer.WriteNumber("sequence", sequence);
    });

    public static byte[] RowDetails(ConversationRow row, string version, long sequence) => GatewayJson.Object(writer =>
    {
        writer.WritePropertyName("row");
        JsonSerializer.Serialize(writer, row, GatewayJson.Options);
        writer.WriteString("version", version);
        writer.WriteNumber("sequence", sequence);
    });

    public static byte[] StaleDetails(string version, long sequence) => GatewayJson.Object(writer =>
    {
        writer.WriteString("error", "stale_details");
        writer.WriteString("version", version);
        writer.WriteNumber("sequence", sequence);
    });

    private static void WriteRow(Utf8JsonWriter writer, CompactWireRow row)
    {
        if (row.Summary is { } summary)
        {
            writer.WriteStartObject();
            writer.WriteString("$type", "turn_summary");
            writer.WriteString("rowId", summary.User.RowId);
            writer.WritePropertyName("user");
            JsonSerializer.Serialize<ConversationRow>(writer, summary.User, GatewayJson.Options);
            writer.WritePropertyName("answer");
            WriteAssistant(writer, summary.Answer, includeReasoning: false);
            writer.WriteString("duration", summary.Duration.ToString("c", CultureInfo.InvariantCulture));
            writer.WriteNumber("detailCount", summary.DetailCount);
            writer.WriteString("detailVersion", summary.Version);
            writer.WriteEndObject();
            return;
        }
        switch (row.Entry.Row)
        {
            case ToolCallRow tool:
                WriteTool(writer, tool, row.Entry.Version);
                break;
            case AssistantMessageRow assistant:
                WriteAssistant(writer, assistant, includeReasoning: true);
                break;
            default:
                JsonSerializer.Serialize(writer, row.Entry.Row, GatewayJson.Options);
                break;
        }
    }

    private static void WriteAssistant(Utf8JsonWriter writer, AssistantMessageRow assistant, bool includeReasoning)
    {
        // Tool payloads have their own deferred rows and must not leak through message parts.
        ConversationRow visible = assistant with
        {
            Parts = assistant.Parts.Where(part => part is TextPart or FilePart || includeReasoning && part is ReasoningPart).ToArray(),
        };
        JsonSerializer.Serialize(writer, visible, GatewayJson.Options);
    }

    private static void WriteTool(Utf8JsonWriter writer, ToolCallRow tool, string version)
    {
        writer.WriteStartObject();
        writer.WriteString("$type", "tool_call");
        writer.WriteString("rowId", tool.RowId);
        writer.WriteString("id", tool.Id.Value);
        writer.WriteString("toolName", tool.ToolName);
        writer.WriteStartObject("input");
        if (tool.Input.ValueKind == JsonValueKind.Object)
        {
            var fields = 0;
            foreach (var name in HeaderFields)
            {
                if (fields == MaxHeaderFields)
                    break;
                if (!tool.Input.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? "";
                    if (text.Length > MaxHeaderLength)
                    {
                        if (name is "path" or "file_path" or "filePath")
                        {
                            var start = text.Length - MaxHeaderLength + 3;
                            if (char.IsLowSurrogate(text[start])) start++;
                            text = "..." + text[start..];
                        }
                        else
                        {
                            var end = MaxHeaderLength - 3;
                            if (char.IsHighSurrogate(text[end - 1])) end--;
                            text = text[..end] + "...";
                        }
                    }
                    writer.WriteString(name, text);
                }
                else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    && value.GetRawText().Length <= MaxHeaderLength)
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
                else
                    continue;
                fields++;
            }
        }
        writer.WriteEndObject();
        writer.WritePropertyName("status");
        JsonSerializer.Serialize(writer, tool.Status, GatewayJson.Options);
        writer.WriteBoolean("isError", tool.IsError);
        writer.WritePropertyName("duration");
        JsonSerializer.Serialize(writer, tool.Duration, GatewayJson.Options);
        writer.WriteString("startedAt", tool.StartedAt);
        writer.WritePropertyName("fileChange");
        JsonSerializer.Serialize(writer, tool.FileChange, GatewayJson.Options);
        writer.WriteBoolean("detailsDeferred", true);
        writer.WriteString("detailVersion", version);
        writer.WriteEndObject();
    }
}
