namespace Alcoler.Remote;

internal sealed class FailureRateLimiter(int maxFailures, TimeSpan window)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Failures)> _windows = new(StringComparer.Ordinal);

    public bool IsAllowed(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_windows.TryGetValue(key, out var entry))
                return true;
            return now - entry.WindowStart >= window || entry.Failures < maxFailures;
        }
    }

    public void RecordFailure(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            var inWindow = _windows.TryGetValue(key, out var current) && now - current.WindowStart < window;
            _windows[key] = inWindow
                ? (current.WindowStart, current.Failures + 1)
                : (now, 1);
        }
    }
}
