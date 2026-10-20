using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Contracts.Tools;

public sealed record ArtifactReference(string Uri, string MimeType, string FileName);

public interface IToolWorkspace
{
    string Root { get; }

    string ResolvePath(string path);

    ValueTask<ArtifactReference> SaveArtifactAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken);
}

public sealed record ToolExecutionContext(
    SessionId SessionId,
    string WorkspaceRoot,
    JsonElement Input,
    IToolWorkspace Workspace,
    TimeProvider Clock,
    ISessionCommands? Commands = null);

public sealed record ToolExecutionResult(
    IReadOnlyList<ToolResultBlock> Content,
    bool IsError = false,
    string? DisplaySummary = null)
{
    public static ToolExecutionResult Text(string text, bool isError = false, string? displaySummary = null) =>
        new([new ToolResultText(text)], isError, displaySummary);
}

public interface ITool
{
    ToolContract Contract { get; }

    ToolMetadata Metadata { get; }

    ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken);

    ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken);

    ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}
