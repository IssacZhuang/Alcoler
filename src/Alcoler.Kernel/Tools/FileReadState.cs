using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Path keys are normalized absolute paths; comparisons are case-insensitive because
/// the tooling targets Windows and macOS workspaces.
/// </summary>
public sealed class FileReadState : IFileReadState
{
    private readonly object _sync = new();
    private Dictionary<string, FileReadSnapshot> _reads = new(StringComparer.OrdinalIgnoreCase);

    public FileReadSnapshot? Find(string path)
    {
        lock (_sync)
        {
            return _reads.TryGetValue(Normalize(path), out var snapshot) ? snapshot : null;
        }
    }

    public IReadOnlyList<(string Path, FileReadSnapshot Snapshot)> TrackedReads()
    {
        lock (_sync)
        {
            return [.. _reads.Select(static entry => (entry.Key, entry.Value))];
        }
    }

    public void RecordRead(string path, bool completeView)
    {
        lock (_sync)
        {
            _reads[Normalize(path)] = new FileReadSnapshot(
                Length(path),
                WriteTime(path),
                completeView);
        }
    }

    public void RecordWrite(string path)
    {
        lock (_sync)
        {
            // The post-write state is what the model has in context, so it counts as a read.
            _reads[Normalize(path)] = new FileReadSnapshot(
                Length(path),
                WriteTime(path),
                CompleteView: true);
        }
    }

    public void Remove(string path)
    {
        lock (_sync)
        {
            _reads.Remove(Normalize(path));
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static DateTimeOffset WriteTime(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
