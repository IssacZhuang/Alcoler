using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Path keys are normalized absolute paths; comparisons are case-insensitive because
/// the tooling targets Windows and macOS workspaces.
/// </summary>
public sealed class FileReadState : IFileReadState
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FileReadSnapshot> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<(int Offset, int? Limit), FileReadSnapshot>> _views = new(StringComparer.OrdinalIgnoreCase);

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

    public FileReadSnapshot? FindView(string path, int offset, int? limit)
    {
        lock (_sync)
            return _views.TryGetValue(Normalize(path), out var views) && views.TryGetValue((offset, limit), out var snapshot)
                ? snapshot : null;
    }

    public void RecordRead(string path, bool completeView) => RecordReadView(path, completeView, 1, null);

    public void RecordReadView(string path, bool completeView, int offset, int? limit, string? contentHash = null)
    {
        lock (_sync)
        {
            var key = Normalize(path);
            var snapshot = new FileReadSnapshot(Length(path), WriteTime(path), completeView, offset, limit, contentHash);
            _reads[key] = snapshot;
            if (!_views.TryGetValue(key, out var views))
                _views[key] = views = [];
            views[(offset, limit)] = snapshot;
        }
    }

    public void RecordWrite(string path)
    {
        lock (_sync)
        {
            // The post-write state is what the model has in context, so it counts as a read.
            var key = Normalize(path);
            var snapshot = new FileReadSnapshot(Length(path), WriteTime(path), CompleteView: true);
            _reads[key] = snapshot;
            _views[key] = new() { [(1, null)] = snapshot };
        }
    }

    public void Remove(string path)
    {
        lock (_sync)
        {
            _reads.Remove(Normalize(path));
            _views.Remove(Normalize(path));
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _reads.Clear();
            _views.Clear();
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
