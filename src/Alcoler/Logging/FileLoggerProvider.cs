using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Alcoler.Logging;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxFileBytes = 10 * 1024 * 1024;
    private const long DefaultMaxTotalBytes = 50 * 1024 * 1024;
    private const int MaxQueuedEntries = 1024;
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly long _maxFileBytes;
    private readonly long _maxTotalBytes;
    private readonly TimeSpan _retention;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;
    private int _disposed;

    public FileLoggerProvider(
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        long maxFileBytes = DefaultMaxFileBytes,
        long maxTotalBytes = DefaultMaxTotalBytes,
        TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _minimumLevel = minimumLevel;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _maxTotalBytes = Math.Max(_maxFileBytes, maxTotalBytes);
        _retention = retention ?? DefaultRetention;
        Directory.CreateDirectory(_directory);
        Prune();
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(MaxQueuedEntries)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(LogEntry.ForFlush(signal)))
        {
            return;
        }
        try
        {
            signal.Task.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Enqueue(string category, LogLevel level, string message, Exception? exception) =>
        _channel.Writer.TryWrite(new LogEntry(DateTimeOffset.Now, level, category, message, exception, null));

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? path = null;
        DateOnly? date = null;
        long size = 0;
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    if (entry.FlushSignal is { } signal)
                    {
                        try
                        {
                            writer?.Flush();
                        }
                        catch (Exception)
                        {
                        }
                        writer?.Dispose();
                        writer = null;
                        date = null;
                        signal.TrySetResult();
                        continue;
                    }

                    try
                    {
                        var entryDate = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
                        if (writer is null || date != entryDate || size >= _maxFileBytes)
                        {
                            writer?.Dispose();
                            writer = null;
                            (writer, path) = OpenWriter(entryDate);
                            date = entryDate;
                            size = new FileInfo(path).Length;
                            Prune(path);
                        }

                        var line = Format(entry);
                        writer.WriteLine(line);
                        size += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    }
                    catch (Exception)
                    {
                        // Losing log lines is preferable to taking down the process or stalling the queue.
                        writer?.Dispose();
                        writer = null;
                        date = null;
                    }
                }

                // Close between batches so external readers can open the file while the app runs.
                try
                {
                    writer?.Flush();
                }
                catch (Exception)
                {
                }
                writer?.Dispose();
                writer = null;
                date = null;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private (StreamWriter Writer, string Path) OpenWriter(DateOnly date)
    {
        var prefix = $"alcoler-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(_directory, $"{prefix}.log");
        var sequence = 1;
        while (File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes)
        {
            sequence++;
            path = Path.Combine(_directory, $"{prefix}-{sequence}.log");
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (writer, path);
    }

    private void Prune(string? exceptPath = null)
    {
        try
        {
            var cutoff = DateTime.UtcNow - _retention;
            var files = Directory.GetFiles(_directory, "alcoler-*.log")
                .Where(path => exceptPath is null || !string.Equals(path, exceptPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
            {
                TryDelete(file);
            }

            var remaining = files.Where(file => file.Exists).ToList();
            var total = remaining.Sum(file => file.Length);
            foreach (var file in remaining)
            {
                if (total <= _maxTotalBytes)
                {
                    break;
                }
                total -= file.Length;
                TryDelete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception)
        {
        }
    }

    private static string Format(LogEntry entry)
    {
        var builder = new StringBuilder(128 + (entry.Exception?.ToString().Length ?? 0));
        builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(LevelTag(entry.Level))
            .Append("] ")
            .Append(entry.Category)
            .Append(": ")
            .Append(entry.Message);
        if (entry.Exception is not null)
        {
            builder.AppendLine().Append(entry.Exception);
        }
        return builder.ToString();
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON",
    };

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        LogLevel Level,
        string Category,
        string Message,
        Exception? Exception,
        TaskCompletionSource? FlushSignal)
    {
        public static LogEntry ForFlush(TaskCompletionSource signal) =>
            new(default, LogLevel.None, string.Empty, string.Empty, null, signal);
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!provider.IsEnabled(logLevel))
            {
                return;
            }

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception)
            {
                message = state?.ToString() ?? string.Empty;
            }
            provider.Enqueue(category, logLevel, message, exception);
        }
    }
}
