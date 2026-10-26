using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Alcoler.Browser.Chromium;

public interface IChromiumProcess : IDisposable
{
    Uri WsUri { get; }

    event EventHandler? Exited;

    Task StartAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}

public sealed class ChromiumProcess : IChromiumProcess
{
    private const int StartupTimeoutMs = 20000;
    private const int PollIntervalMs = 100;
    private const int ShutdownGraceMs = 5000;
    private const int StderrTailChars = 4096;

    private readonly string _executablePath;
    private readonly string _userDataDirectory;
    private readonly bool _headless;
    private readonly StringBuilder _stderrTail = new();
    private readonly Lock _sync = new();
    private Process? _process;
    private Uri? _wsUri;
    private bool _disposed;

    public ChromiumProcess(string executablePath, string userDataDirectory, bool headless)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        _executablePath = executablePath;
        _userDataDirectory = userDataDirectory;
        _headless = headless;
    }

    public event EventHandler? Exited;

    public Uri WsUri => _wsUri ?? throw new InvalidOperationException("The browser process has not started yet.");

    public static IReadOnlyList<string> BuildArguments(string userDataDirectory, bool headless)
    {
        // --no-sandbox must never be added: it disables the browser's OS-level process isolation.
        var arguments = new List<string>
        {
            "--remote-debugging-port=0",
            $"--user-data-dir={userDataDirectory}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-session-crash-bubble",
            "--hide-crash-restore-bubble",
        };
        if (headless) arguments.Add("--headless=new");
        return arguments;
    }

    public static (int Port, string BrowserWebSocketPath)? ParseDevToolsActivePort(string content)
    {
        var lines = content.Split('\n', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        if (!int.TryParse(lines[0], out var port) || port is < 1 or > 65535) return null;
        var path = lines[1];
        if (path.Length == 0 || path[0] != '/' || path.Contains(' ')) return null;
        return (port, path);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null) return;
        Directory.CreateDirectory(_userDataDirectory);
        // A killed predecessor leaves a port file pointing at its dead socket; it must not satisfy this poll.
        var portFile = Path.Combine(_userDataDirectory, "DevToolsActivePort");
        try { File.Delete(portFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var start = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
        };
        foreach (var argument in BuildArguments(_userDataDirectory, _headless)) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start the browser process '{_executablePath}'.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start the browser process '{_executablePath}': {ex.Message}", ex);
        }
        _ = PumpStderrAsync(process);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        lock (_sync) _process = process;

        var deadline = Environment.TickCount64 + StartupTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The browser process exited during startup with code {process.ExitCode}. Standard error tail: {StderrTail()}");
            }
            if (File.Exists(portFile))
            {
                try
                {
                    var parsed = ParseDevToolsActivePort(await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false));
                    if (parsed is { } ready)
                    {
                        _wsUri = new Uri($"ws://127.0.0.1:{ready.Port}{ready.BrowserWebSocketPath}");
                        return;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            $"The browser did not expose a DevTools port within {StartupTimeoutMs / 1000} seconds. Standard error tail: {StderrTail()}");
    }

    private string StderrTail()
    {
        lock (_sync) return _stderrTail.ToString();
    }

    private async Task PumpStderrAsync(Process process)
    {
        var buffer = new char[512];
        try
        {
            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0) break;
                lock (_sync)
                {
                    _stderrTail.Append(buffer, 0, read);
                    if (_stderrTail.Length > StderrTailChars) _stderrTail.Remove(0, _stderrTail.Length - StderrTailChars);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_sync) process = _process;
        if (process is null || process.HasExited) return;
        // The caller sends CDP Browser.close first; this grace window bounds a browser that refuses to exit.
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(ShutdownGraceMs);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(ShutdownGraceMs / 1000.0), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            process.Dispose();
        }
    }
}
