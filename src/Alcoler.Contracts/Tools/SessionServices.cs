using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Contracts.Tools;

public sealed record FileReadSnapshot(long LengthBytes, DateTimeOffset LastWriteUtc, bool CompleteView)
{
    public bool MatchesCurrent(long lengthBytes, DateTimeOffset lastWriteUtc) =>
        LengthBytes == lengthBytes && LastWriteUtc == lastWriteUtc;
}

/// <summary>
/// Tracks which files the model has read this session so Write/Edit can refuse stale
/// overwrites and Read can suppress repeated reads of unchanged files.
/// </summary>
public interface IFileReadState
{
    FileReadSnapshot? Find(string path);

    IReadOnlyList<(string Path, FileReadSnapshot Snapshot)> TrackedReads();

    void RecordRead(string path, bool completeView);

    void RecordWrite(string path);

    void Remove(string path);
}

/// <summary>
/// Working directory that persists across Bash calls within one session.
/// </summary>
public interface IShellSessionState
{
    string CurrentDirectory { get; }

    void SetCurrentDirectory(string path);
}

public enum BackgroundJobStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
}

public sealed record BackgroundJobInfo(
    BackgroundJobId Id,
    string Description,
    DateTimeOffset StartedAt,
    BackgroundJobStatus Status,
    int? ExitCode = null,
    string? OutputPath = null,
    string? Error = null);

public sealed record BackgroundJobResult(int ExitCode, string? Error = null);

/// <summary>
/// Detached jobs (e.g. background Bash) that outlive the tool call which started them.
/// Completion is surfaced to the session as a system notification so the model is re-invoked.
/// </summary>
public interface IBackgroundJobService
{
    BackgroundJobInfo Start(string description, string? outputPath, Func<CancellationToken, Task<BackgroundJobResult>> run);

    IReadOnlyList<BackgroundJobInfo> List();

    BackgroundJobInfo? Find(BackgroundJobId id);

    bool Stop(BackgroundJobId id);
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

public sealed record SkillDocument(string Name, string Description, string BaseDirectory, string Content);

/// <summary>
/// Loads the body of a skill listed in the session's skills listing.
/// </summary>
public interface ISkillContentProvider
{
    SkillDocument? TryLoad(string name, CancellationToken cancellationToken);
}
