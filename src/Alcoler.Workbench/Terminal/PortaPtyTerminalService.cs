using System.Runtime.InteropServices;
using System.Text;
using Porta.Pty;

namespace Alcoler.Workbench.Terminal;

/// <summary>
/// PTY shell on Porta.Pty. The read loop owns the ReaderStream for the process
/// lifetime; after <see cref="StopAsync"/> or process exit the connection is disposed.
/// </summary>
public sealed class PortaPtyTerminalService : IPtyTerminalService
{
    public static int DefaultCols => 120;
    public static int DefaultRows => 30;

    private readonly object _lifecycleGate = new();
    private readonly Func<PtyOptions, CancellationToken, Task<IPtyConnection>> _spawnAsync;
    private IPtyConnection? _connection;
    private CancellationTokenSource? _readCancellation;
    private Task? _readLoop;
    private bool _disposed;

    public bool IsRunning => _connection is not null && !_exited;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    private bool _exited;

    public PortaPtyTerminalService() : this(PtyProvider.SpawnAsync)
    {
    }

    internal PortaPtyTerminalService(Func<PtyOptions, CancellationToken, Task<IPtyConnection>> spawnAsync)
    {
        _spawnAsync = spawnAsync;
    }

    public async Task StartAsync(string workingDirectory, int cols, int rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            throw new InvalidOperationException("Terminal is already running.");
        }

        var shell = ResolveShell();
        var options = new PtyOptions
        {
            Name = "Alcoler",
            Cols = cols,
            Rows = rows,
            Cwd = workingDirectory,
            App = shell,
            Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
            },
        };

        var connection = await _spawnAsync(options, cancellationToken);
        lock (_lifecycleGate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                try
                {
                    connection.Kill();
                }
                finally
                {
                    connection.Dispose();
                }
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
            _connection = connection;
            _exited = false;
            _readCancellation = new CancellationTokenSource();
            var readToken = _readCancellation.Token;
            connection.ProcessExited += OnProcessExited;
            _readLoop = Task.Run(() => ReadLoopAsync(connection, readToken));
        }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        var stream = WriterStream ?? throw new InvalidOperationException("Terminal is not running.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Resize(int cols, int rows) => _connection?.Resize(cols, rows);

    public async Task StopAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }
        _readCancellation?.Cancel();
        connection.Kill();
        connection.WaitForExit(3000);
        if (_readLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        DisposeConnection();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _readCancellation?.Cancel();
            try
            {
                _connection?.Kill();
            }
            catch (ObjectDisposedException)
            {
            }
            DisposeConnection();
            _readCancellation?.Dispose();
            _readCancellation = null;
        }
    }

    private Stream? WriterStream => _connection?.WriterStream;

    private void DisposeConnection()
    {
        if (_connection is not { } connection)
        {
            return;
        }
        connection.ProcessExited -= OnProcessExited;
        connection.Dispose();
        _connection = null;
    }

    private async Task ReadLoopAsync(IPtyConnection connection, CancellationToken cancellationToken)
    {
        // Decoder must survive chunk boundaries: a UTF-8 sequence can split across reads.
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[4096];
        var chars = new char[8192];
        var stream = connection.ReaderStream;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                var decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                if (decoded > 0)
                {
                    OutputReceived?.Invoke(this, new string(chars, 0, decoded));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _exited = true;
        var connection = _connection;
        if (connection is null)
        {
            return;
        }
        Exited?.Invoke(this, connection.ExitCode);
    }

    internal static string ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FindOnPath("pwsh.exe")
                ?? FindOnPath("powershell.exe")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell) ? "/bin/sh" : shell;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return fileName == "powershell.exe"
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe")
            : null;
    }
}
