using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
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
    ISessionCommands? Commands = null,
    IFileReadState? FileReads = null,
    IShellSessionState? Shell = null,
    IBackgroundJobService? BackgroundJobs = null,
    IAuxModelClient? AuxModel = null,
    ISkillContentProvider? Skills = null,
    IUserInteractionPort? Interactions = null,
    ToolCallId? ToolCallId = null,
    ISubagentTasks? Subagents = null,
    string? ToolOutputRoot = null,
    IProviderWebSearch? ProviderSearch = null);

public sealed record FileChangeStats(int Added, int Removed);

public sealed record ToolExecutionResult(
    IReadOnlyList<ToolResultBlock> Content,
    bool IsError = false,
    string? DisplaySummary = null,
    bool StopTurnAfterResult = false,
    string? StructuredOutput = null,
    FileChangeStats? FileChange = null,
    string? FollowUpUserInput = null,
    string? FileChangePath = null)
{
    public static ToolExecutionResult Text(
        string text,
        bool isError = false,
        string? displaySummary = null,
        bool stopTurnAfterResult = false,
        string? structuredOutput = null,
        FileChangeStats? fileChange = null,
        string? followUpUserInput = null,
        string? fileChangePath = null) =>
        new([new ToolResultText(text)], isError, displaySummary, stopTurnAfterResult, structuredOutput, fileChange,
            followUpUserInput, fileChangePath);
}

public interface ITool
{
    ToolContract Contract { get; }

    JsonElement RuntimeInputSchema => Contract.InputSchema;

    ToolMetadata Metadata { get; }

    ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken);

    ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken);

    ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Lets a tool downgrade the permission flow for specific inputs (e.g. Bash commands
/// classified as read-only), mirroring per-call permission semantics instead of
/// forcing one verdict for every call of the tool.
/// </summary>
public interface IToolInputPolicy
{
    bool IsReadOnlyInput(JsonElement input);
}
