using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Subagents;

namespace Alcoler.Contracts.Tools;

public sealed record FileReadSnapshot(
    long LengthBytes,
    DateTimeOffset LastWriteUtc,
    bool CompleteView,
    int Offset = 1,
    int? Limit = null,
    string? ContentHash = null)
{
    public bool MatchesCurrent(long lengthBytes, DateTimeOffset lastWriteUtc) =>
        LengthBytes == lengthBytes && LastWriteUtc.ToUnixTimeMilliseconds() == lastWriteUtc.ToUnixTimeMilliseconds();
}

/// <summary>
/// Tracks which files the model has read this session so Write/Edit can refuse stale
/// overwrites and Read can suppress repeated reads of unchanged files.
/// </summary>
public interface IFileReadState
{
    FileReadSnapshot? Find(string path);

    FileReadSnapshot? FindView(string path, int offset, int? limit) =>
        Find(path) is { } snapshot && snapshot.Offset == offset && snapshot.Limit == limit ? snapshot : null;

    IReadOnlyList<(string Path, FileReadSnapshot Snapshot)> TrackedReads();

    void RecordRead(string path, bool completeView);

    void RecordReadView(string path, bool completeView, int offset, int? limit, string? contentHash = null) => RecordRead(path, completeView);

    void RecordWrite(string path);

    void Remove(string path);
}

/// <summary>
/// Working directory that persists across Bash calls within one session.
/// </summary>
public interface IShellSessionState
{
    string CurrentDirectory { get; }

    ShellEnvironment? Environment => null;

    void SetCurrentDirectory(string path);
}

public enum BackgroundJobStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
    Lost,
}

public sealed record BackgroundJobInfo(
    BackgroundJobId Id,
    string Description,
    DateTimeOffset StartedAt,
    BackgroundJobStatus Status,
    int? ExitCode = null,
    string? OutputPath = null,
    string? Error = null,
    ToolCallId? ToolUseId = null);

public sealed record BackgroundJobResult(int? ExitCode, string? Error = null);

/// <summary>
/// Detached jobs (e.g. background Bash) that outlive the tool call which started them.
/// Completion is surfaced to the session as a system notification so the model is re-invoked.
/// </summary>
public interface IBackgroundJobService
{
    BackgroundJobInfo Start(
        string description,
        string? outputPath,
        Func<CancellationToken, Task<BackgroundJobResult>> run,
        ToolCallId? toolUseId = null);

    IReadOnlyList<BackgroundJobInfo> List();

    BackgroundJobInfo? Find(BackgroundJobId id);

    bool Stop(BackgroundJobId id);
}

public sealed record SubagentTaskInfo(
    SubagentId Id,
    SessionId ChildSessionId,
    string AgentType,
    string Description,
    string Prompt,
    string? OutputFile,
    SubagentStatus Status,
    string? Result,
    string? Error,
    int? ToolUses,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    public bool Notified { get; init; }
}

/// <summary>
/// Runtime view of local_agent tasks so TaskOutput/TaskStop can address foreground and
/// background subagent runs by agent id, and a consumed terminal result can suppress the
/// duplicate background completion notification.
/// </summary>
public interface ISubagentTasks
{
    SubagentTaskInfo? Find(string taskId);

    bool TryMarkNotified(string taskId);

    ValueTask<bool> TryStopAsync(string taskId, CancellationToken cancellationToken);
}

public sealed record AuxModelRequest(string SystemPrompt, string UserPrompt, int MaxOutputTokens);

/// <summary>
/// One-shot text completion on the session's configured model for auxiliary tasks
/// (WebFetch page summarization, session-context extraction). Never carries tools.
/// </summary>
public interface IAuxModelClient
{
    ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken);
}

public sealed record SkillDocument(string Name, string Description, string BaseDirectory, string Content, bool Truncated = false);

/// <summary>
/// Loads the body of a skill listed in the session's skills listing.
/// </summary>
public interface ISkillContentProvider
{
    SkillDocument? TryLoad(string name, CancellationToken cancellationToken);
}
