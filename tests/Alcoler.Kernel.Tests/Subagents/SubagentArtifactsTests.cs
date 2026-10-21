using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Subagents;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public sealed class SubagentArtifactsTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly SubagentId AgentId = new("sub_test");
    private static readonly SessionId ParentSessionId = new("sess_parent");
    private static readonly SessionId ChildSessionId = new("sess_child");
    private static readonly ToolCallId ParentToolUseId = new("call_parent");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "alcoler-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly SubagentArtifacts _artifacts;

    public SubagentArtifactsTests() => _artifacts = new SubagentArtifacts(_root);

    [Fact]
    public void WriteCompleted_CreatesLineageDirectoryTextCopiesAndMetadata()
    {
        _artifacts.WriteCompleted(
            AgentId, ChildSessionId, ParentSessionId, ParentToolUseId, "Explore", "find files", "find all files",
            @"C:\workspace", "final answer", totalDurationMs: 1234, totalToolUseCount: 2,
            createdAt: CreatedAt, completedAt: CreatedAt.AddSeconds(3));

        var directory = Path.Combine(_root, ParentSessionId.Value, AgentId.Value);
        var outputFile = Path.Combine(directory, "output.txt");
        var taskOutputFile = Path.Combine(directory, "task.output");
        var metadataFile = Path.Combine(directory, "metadata.json");
        Assert.Equal(outputFile, _artifacts.GetOutputFile(ParentSessionId, AgentId));
        Assert.Equal(taskOutputFile, _artifacts.GetTaskOutputFile(ParentSessionId, AgentId));
        Assert.Equal(metadataFile, _artifacts.GetMetadataFile(ParentSessionId, AgentId));
        Assert.Equal("final answer", File.ReadAllText(outputFile));
        Assert.Equal(File.ReadAllBytes(outputFile), File.ReadAllBytes(taskOutputFile));

        var json = File.ReadAllText(metadataFile);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.Contains("\n  \"agentId\": \"sub_test\",", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement;
        Assert.Equal("sub_test", metadata.GetProperty("agentId").GetString());
        Assert.Equal("sess_child", metadata.GetProperty("childSessionId").GetString());
        Assert.Equal("sess_parent", metadata.GetProperty("parentSessionId").GetString());
        Assert.Equal("call_parent", metadata.GetProperty("parentToolUseId").GetString());
        Assert.Equal("Explore", metadata.GetProperty("agentType").GetString());
        Assert.Equal("find files", metadata.GetProperty("description").GetString());
        Assert.Equal("find all files", metadata.GetProperty("prompt").GetString());
        Assert.Equal("completed", metadata.GetProperty("status").GetString());
        Assert.Equal(CreatedAt, DateTimeOffset.Parse(metadata.GetProperty("createdAt").GetString()!));
        Assert.Equal(CreatedAt.AddSeconds(3), DateTimeOffset.Parse(metadata.GetProperty("updatedAt").GetString()!));
        Assert.Equal(CreatedAt.AddSeconds(3), DateTimeOffset.Parse(metadata.GetProperty("completedAt").GetString()!));
        Assert.Equal(@"C:\workspace", metadata.GetProperty("workspaceRoot").GetString());
        Assert.Equal(outputFile, metadata.GetProperty("outputFile").GetString());
        Assert.Equal(taskOutputFile, metadata.GetProperty("taskOutputFile").GetString());
        Assert.Equal(metadataFile, metadata.GetProperty("metadataFile").GetString());
        Assert.Equal(1234, metadata.GetProperty("totalDurationMs").GetInt32());
        Assert.Equal(2, metadata.GetProperty("totalToolUseCount").GetInt32());
        Assert.False(metadata.TryGetProperty("error", out _));
    }

    [Fact]
    public void WriteRunning_WritesNullParentToolUseIdAndNoTerminalFields()
    {
        _artifacts.WriteRunning(
            AgentId, ChildSessionId, ParentSessionId, parentToolUseId: null, "general-purpose", "description",
            "prompt", @"C:\workspace", CreatedAt, CreatedAt.AddMilliseconds(5));

        using var document = JsonDocument.Parse(
            File.ReadAllText(_artifacts.GetMetadataFile(ParentSessionId, AgentId)));
        var metadata = document.RootElement;
        Assert.Equal("running", metadata.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("parentToolUseId").ValueKind);
        Assert.False(metadata.TryGetProperty("completedAt", out _));
        Assert.False(metadata.TryGetProperty("totalDurationMs", out _));
        Assert.False(metadata.TryGetProperty("totalToolUseCount", out _));
        Assert.False(metadata.TryGetProperty("resumedAt", out _));
        Assert.False(File.Exists(_artifacts.GetOutputFile(ParentSessionId, AgentId)));
    }

    [Fact]
    public void WriteFailed_WritesErrorTextAndFailureMetadata()
    {
        _artifacts.WriteFailed(
            AgentId, ChildSessionId, ParentSessionId, ParentToolUseId, "general-purpose", "description",
            "prompt", @"C:\workspace", "provider rejected request", CreatedAt, CreatedAt.AddSeconds(1));

        Assert.Equal(
            "provider rejected request",
            File.ReadAllText(_artifacts.GetOutputFile(ParentSessionId, AgentId)));
        Assert.Equal(
            File.ReadAllText(_artifacts.GetOutputFile(ParentSessionId, AgentId)),
            File.ReadAllText(_artifacts.GetTaskOutputFile(ParentSessionId, AgentId)));
        using var document = JsonDocument.Parse(
            File.ReadAllText(_artifacts.GetMetadataFile(ParentSessionId, AgentId)));
        var metadata = document.RootElement;
        Assert.Equal("failed", metadata.GetProperty("status").GetString());
        Assert.Equal("provider rejected request", metadata.GetProperty("error").GetString());
        Assert.Equal(CreatedAt.AddSeconds(1), DateTimeOffset.Parse(metadata.GetProperty("completedAt").GetString()!));
        Assert.False(metadata.TryGetProperty("totalToolUseCount", out _));
    }

    [Fact]
    public void WriteStopped_WritesFixedSentenceAndStoppedMetadata()
    {
        _artifacts.WriteStopped(
            AgentId, ChildSessionId, ParentSessionId, ParentToolUseId, "general-purpose", "description",
            "prompt", @"C:\workspace", CreatedAt, CreatedAt.AddSeconds(1));

        Assert.Equal("Background agent task stopped.\n", File.ReadAllText(
            _artifacts.GetOutputFile(ParentSessionId, AgentId)));
        Assert.Equal("Background agent task stopped.\n", File.ReadAllText(
            _artifacts.GetTaskOutputFile(ParentSessionId, AgentId)));
        using var document = JsonDocument.Parse(
            File.ReadAllText(_artifacts.GetMetadataFile(ParentSessionId, AgentId)));
        Assert.Equal("stopped", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void WriteRunning_OnResume_RewritesStatusAndAddsResumeLineage()
    {
        _artifacts.WriteCompleted(
            AgentId, ChildSessionId, ParentSessionId, ParentToolUseId, "general-purpose", "description",
            "prompt", @"C:\workspace", "first answer", totalDurationMs: 10, totalToolUseCount: 1,
            createdAt: CreatedAt, completedAt: CreatedAt.AddSeconds(1));

        var resumedAt = CreatedAt.AddMinutes(5);
        _artifacts.WriteRunning(
            AgentId, ChildSessionId, ParentSessionId, ParentToolUseId, "general-purpose", "description",
            "prompt", @"C:\workspace", createdAt: resumedAt, updatedAt: resumedAt,
            resumedAt: resumedAt, resumedFromToolCallId: new ToolCallId("call_resume"));

        using var document = JsonDocument.Parse(
            File.ReadAllText(_artifacts.GetMetadataFile(ParentSessionId, AgentId)));
        var metadata = document.RootElement;
        Assert.Equal("running", metadata.GetProperty("status").GetString());
        Assert.Equal(resumedAt, DateTimeOffset.Parse(metadata.GetProperty("resumedAt").GetString()!));
        Assert.Equal("call_resume", metadata.GetProperty("resumedFromToolCallId").GetString());
        Assert.Equal(resumedAt, DateTimeOffset.Parse(metadata.GetProperty("createdAt").GetString()!));
        Assert.False(metadata.TryGetProperty("completedAt", out _));
        Assert.Equal("first answer", File.ReadAllText(_artifacts.GetOutputFile(ParentSessionId, AgentId)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
