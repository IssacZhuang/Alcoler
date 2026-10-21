using System.Globalization;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Kernel.Subagents;

/// <summary>
/// Per-subagent transcript directory: one folder per parent session and agent holding
/// output.txt, a byte-identical task.output copy, and the lineage metadata.
/// </summary>
public sealed class SubagentArtifacts
{
    public const string StoppedText = "Background agent task stopped.\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _rootDirectory;

    public SubagentArtifacts(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string GetOutputFile(SessionId parentSessionId, SubagentId agentId) =>
        Path.Combine(GetDirectory(parentSessionId, agentId), "output.txt");

    public string GetTaskOutputFile(SessionId parentSessionId, SubagentId agentId) =>
        Path.Combine(GetDirectory(parentSessionId, agentId), "task.output");

    public string GetMetadataFile(SessionId parentSessionId, SubagentId agentId) =>
        Path.Combine(GetDirectory(parentSessionId, agentId), "metadata.json");

    public void WriteRunning(
        SubagentId agentId,
        SessionId childSessionId,
        SessionId parentSessionId,
        ToolCallId? parentToolUseId,
        string agentType,
        string description,
        string prompt,
        string workspaceRoot,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? resumedAt = null,
        ToolCallId? resumedFromToolCallId = null)
    {
        var metadata = CreateMetadata(
            agentId, childSessionId, parentSessionId, parentToolUseId, agentType, description, prompt,
            "running", createdAt, updatedAt, workspaceRoot);
        if (resumedAt is { } resumed)
            metadata["resumedAt"] = Timestamp(resumed);
        if (resumedFromToolCallId is { } resumedFrom)
            metadata["resumedFromToolCallId"] = resumedFrom.Value;
        WriteMetadata(metadata, parentSessionId, agentId);
    }

    public void WriteCompleted(
        SubagentId agentId,
        SessionId childSessionId,
        SessionId parentSessionId,
        ToolCallId? parentToolUseId,
        string agentType,
        string description,
        string prompt,
        string workspaceRoot,
        string text,
        int totalDurationMs,
        int totalToolUseCount,
        DateTimeOffset createdAt,
        DateTimeOffset completedAt)
    {
        WriteText(text, parentSessionId, agentId);
        var metadata = CreateMetadata(
            agentId, childSessionId, parentSessionId, parentToolUseId, agentType, description, prompt,
            "completed", createdAt, completedAt, workspaceRoot);
        metadata["completedAt"] = Timestamp(completedAt);
        metadata["totalDurationMs"] = totalDurationMs;
        metadata["totalToolUseCount"] = totalToolUseCount;
        WriteMetadata(metadata, parentSessionId, agentId);
    }

    public void WriteFailed(
        SubagentId agentId,
        SessionId childSessionId,
        SessionId parentSessionId,
        ToolCallId? parentToolUseId,
        string agentType,
        string description,
        string prompt,
        string workspaceRoot,
        string error,
        DateTimeOffset createdAt,
        DateTimeOffset completedAt)
    {
        WriteText(error, parentSessionId, agentId);
        var metadata = CreateMetadata(
            agentId, childSessionId, parentSessionId, parentToolUseId, agentType, description, prompt,
            "failed", createdAt, completedAt, workspaceRoot);
        metadata["completedAt"] = Timestamp(completedAt);
        metadata["error"] = error;
        WriteMetadata(metadata, parentSessionId, agentId);
    }

    public void WriteStopped(
        SubagentId agentId,
        SessionId childSessionId,
        SessionId parentSessionId,
        ToolCallId? parentToolUseId,
        string agentType,
        string description,
        string prompt,
        string workspaceRoot,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        WriteText(StoppedText, parentSessionId, agentId);
        var metadata = CreateMetadata(
            agentId, childSessionId, parentSessionId, parentToolUseId, agentType, description, prompt,
            "stopped", createdAt, updatedAt, workspaceRoot);
        WriteMetadata(metadata, parentSessionId, agentId);
    }

    private string GetDirectory(SessionId parentSessionId, SubagentId agentId) =>
        Path.Combine(_rootDirectory, parentSessionId.Value, agentId.Value);

    private void WriteText(string text, SessionId parentSessionId, SubagentId agentId)
    {
        var outputFile = GetOutputFile(parentSessionId, agentId);
        var taskOutputFile = GetTaskOutputFile(parentSessionId, agentId);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        File.WriteAllText(outputFile, text);
        File.Copy(outputFile, taskOutputFile, overwrite: true);
    }

    private void WriteMetadata(Dictionary<string, object?> metadata, SessionId parentSessionId, SubagentId agentId)
    {
        var metadataFile = GetMetadataFile(parentSessionId, agentId);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataFile)!);
        File.WriteAllText(metadataFile, JsonSerializer.Serialize(metadata, JsonOptions) + "\n");
    }

    private Dictionary<string, object?> CreateMetadata(
        SubagentId agentId,
        SessionId childSessionId,
        SessionId parentSessionId,
        ToolCallId? parentToolUseId,
        string agentType,
        string description,
        string prompt,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string workspaceRoot)
    {
        var outputFile = GetOutputFile(parentSessionId, agentId);
        return new Dictionary<string, object?>
        {
            ["agentId"] = agentId.Value,
            ["childSessionId"] = childSessionId.Value,
            ["parentSessionId"] = parentSessionId.Value,
            ["parentToolUseId"] = parentToolUseId?.Value,
            ["agentType"] = agentType,
            ["description"] = description,
            ["prompt"] = prompt,
            ["status"] = status,
            ["createdAt"] = Timestamp(createdAt),
            ["updatedAt"] = Timestamp(updatedAt),
            ["workspaceRoot"] = workspaceRoot,
            ["outputFile"] = outputFile,
            ["taskOutputFile"] = GetTaskOutputFile(parentSessionId, agentId),
            ["metadataFile"] = GetMetadataFile(parentSessionId, agentId),
        };
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
