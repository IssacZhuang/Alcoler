using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote;

internal sealed class CompactContentVersion : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public static string ForRow(ConversationRow row)
    {
        using var version = new CompactContentVersion();
        version.Add("compact-row-1");
        version.Add(row.RowId);
        switch (row)
        {
            case UserMessageRow user:
                version.Add("user");
                version.Add(user.Text);
                version.Add(user.At);
                version.Add(user.CanRewind);
                version.Add(user.Images?.Count ?? -1);
                if (user.Images is not null)
                    foreach (var image in user.Images) version.Add(image);
                break;
            case AssistantMessageRow assistant:
                version.Add("assistant");
                version.Add(assistant.Id.Value);
                version.Add(assistant.IsStreaming);
                version.Add(assistant.At);
                version.Add(assistant.CanFork);
                version.Add(assistant.Parts.Count);
                foreach (var part in assistant.Parts) version.Add(part);
                break;
            case ToolCallRow tool:
                version.Add("tool");
                version.Add(tool.Id.Value);
                version.Add(tool.ToolName);
                version.Add(tool.Input);
                version.Add((int)tool.Status);
                version.Add(tool.Output);
                version.Add(tool.IsError);
                version.Add(tool.Duration?.Ticks.ToString(CultureInfo.InvariantCulture));
                version.Add(tool.StartedAt);
                version.Add(tool.FileChange is not null);
                if (tool.FileChange is { } change)
                {
                    version.Add(change.Added);
                    version.Add(change.Removed);
                }
                break;
            case FileChangeSummaryRow files:
                version.Add("files");
                version.Add(files.Files.Count);
                foreach (var file in files.Files)
                {
                    version.Add(file.Path);
                    version.Add(file.Added);
                    version.Add(file.Removed);
                }
                version.Add(files.Added);
                version.Add(files.Removed);
                break;
            case SystemNoticeRow notice:
                version.Add("notice");
                version.Add(notice.Kind);
                version.Add(notice.Text);
                version.Add(notice.Subagent is not null);
                if (notice.Subagent is { } agent)
                {
                    version.Add(agent.Id.Value);
                    version.Add(agent.ChildSessionId.Value);
                    version.Add(agent.Description);
                    version.Add((int)agent.Status);
                    version.Add(agent.ResultSummary);
                    version.Add(agent.StartedAt);
                    version.Add(agent.EndedAt?.ToString("O", CultureInfo.InvariantCulture));
                    version.Add(agent.OutputFile);
                    version.Add(agent.ParentToolCallId?.Value);
                    version.Add(agent.AgentType);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported conversation row {row.GetType().Name}.");
        }
        return version.Finish();
    }

    public void Add(string? value)
    {
        Add(value?.Length ?? -1);
        if (value is null)
            return;
        Span<byte> buffer = stackalloc byte[3072];
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var length = Math.Min(1024, remaining.Length);
            if (length < remaining.Length && char.IsHighSurrogate(remaining[length - 1]))
                length--;
            var written = Encoding.UTF8.GetBytes(remaining[..length], buffer);
            _hash.AppendData(buffer[..written]);
            remaining = remaining[length..];
        }
    }

    public void Add(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    private void Add(bool value) => Add(value ? 1 : 0);

    private void Add(DateTimeOffset value) => Add(value.ToString("O", CultureInfo.InvariantCulture));

    private void Add(JsonElement value)
    {
        Add((int)value.ValueKind);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Add(value.GetPropertyCount());
                foreach (var property in value.EnumerateObject())
                {
                    Add(property.Name);
                    Add(property.Value);
                }
                break;
            case JsonValueKind.Array:
                Add(value.GetArrayLength());
                foreach (var item in value.EnumerateArray()) Add(item);
                break;
            case JsonValueKind.String:
                Add(value.GetString());
                break;
            case JsonValueKind.Number:
                Add(value.GetRawText());
                break;
        }
    }

    private void Add(MessagePart part)
    {
        Add(part.Id.Value);
        switch (part)
        {
            case TextPart text:
                Add("text");
                Add(text.Text);
                break;
            case ReasoningPart reasoning:
                Add("reasoning");
                Add(reasoning.Text);
                Add(reasoning.ProviderSignature);
                Add(reasoning.RedactedData);
                Add(reasoning.EncryptedContent);
                Add(reasoning.ProviderItemId);
                break;
            case ToolCallPart call:
                Add("call");
                Add(call.ToolCallId.Value);
                Add(call.ToolName);
                Add(call.Input);
                break;
            case ToolResultPart result:
                Add("result");
                Add(result.ToolCallId.Value);
                Add(result.Content);
                Add(result.IsError);
                Add(result.StructuredOutput);
                break;
            case FilePart file:
                Add("file");
                Add(file.ArtifactUri);
                Add(file.MimeType);
                Add(file.FileName);
                Add(file.ProviderReady);
                break;
            case AttachmentPart attachment:
                Add("attachment");
                Add((int)attachment.Kind);
                Add(attachment.Content);
                Add(attachment.ProviderVisible);
                break;
            default:
                throw new NotSupportedException($"Unsupported message part {part.GetType().Name}.");
        }
    }

    private void Add(IReadOnlyList<ToolResultBlock>? blocks)
    {
        Add(blocks?.Count ?? -1);
        if (blocks is null)
            return;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ToolResultText text:
                    Add("text");
                    Add(text.Text);
                    break;
                case ToolResultMedia media:
                    Add("media");
                    Add(media.ArtifactUri);
                    Add(media.MimeType);
                    Add(media.FileName);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported tool result {block.GetType().Name}.");
            }
        }
    }

    public string Finish() => "c1_" + Convert.ToHexStringLower(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();
}
