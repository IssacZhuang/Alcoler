using System.Collections.Concurrent;
using Alcoler.Terminal;
using Alcoler.Ui;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    Escape,
}

/// <summary>
/// Owns the PTY service and the screen buffer. Service events arrive on a
/// background thread and are marshaled through <see cref="IUiScheduler"/> before
/// mutating <see cref="Buffer"/>, so <see cref="BufferChanged"/> is UI-thread only.
/// </summary>
public sealed partial class TerminalPaneViewModel : ObservableObject, IDisposable
{
    private readonly IPtyTerminalService _service;
    private readonly IUiScheduler _scheduler;
    private readonly VtParser _parser = new();
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly Action _drainOutput;
    private int _outputScheduled;
    private bool _disposed;

    public TerminalPaneViewModel(
        string startedFrom,
        IPtyTerminalService? service = null,
        IUiScheduler? scheduler = null,
        Func<string>? resolveShell = null)
    {
        StartedFrom = startedFrom;
        _service = service ?? new PortaPtyTerminalService(resolveShell);
        _scheduler = scheduler ?? AvaloniaUiScheduler.Instance;
        _drainOutput = DrainOutput;
        Buffer = new TerminalBuffer(PortaPtyTerminalService.DefaultCols, PortaPtyTerminalService.DefaultRows);
        _service.OutputReceived += OnOutputReceived;
        _service.Exited += OnExited;
    }

    public TerminalBuffer Buffer { get; }

    public string StartedFrom { get; }

    public string WorkspaceName => Path.GetFileName(StartedFrom.Replace('\\', '/').TrimEnd('/')) is { Length: > 0 } name
        ? name
        : StartedFrom;

    /// <summary>Raised on the UI thread after the buffer changed; views invalidate on it.</summary>
    public event EventHandler? BufferChanged;

    [ObservableProperty]
    private bool _isConnected;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            return;
        }
        var cols = Buffer.Cols;
        var rows = Buffer.Rows;
        try
        {
            await _service.StartAsync(StartedFrom, cols, rows, CancellationToken.None);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return;
        }
        if (_disposed)
        {
            return;
        }
        IsConnected = true;
        if (cols != Buffer.Cols || rows != Buffer.Rows)
        {
            _service.Resize(Buffer.Cols, Buffer.Rows);
        }
    }

    public async Task StopAsync()
    {
        if (!IsConnected && !_service.IsRunning)
        {
            return;
        }
        await _service.StopAsync();
        IsConnected = false;
    }

    public void SendText(string text)
    {
        if (!IsConnected || text.Length == 0)
        {
            return;
        }
        _ = WriteAsync(text);
    }

    /// <summary>Sends a special key as its VT byte sequence; returns false for unmapped keys.</summary>
    public bool HandleKeyInput(TerminalKey key, bool shift = false, bool control = false)
    {
        if (!IsConnected)
        {
            return false;
        }
        var sequence = key switch
        {
            TerminalKey.Enter => "\r",
            TerminalKey.Tab => shift ? "\x1b[Z" : "\t",
            TerminalKey.Backspace => "\x7f",
            TerminalKey.Delete => "\x1b[3~",
            TerminalKey.Left => control ? "\x1b[1;5D" : "\x1b[D",
            TerminalKey.Right => control ? "\x1b[1;5C" : "\x1b[C",
            TerminalKey.Up => "\x1b[A",
            TerminalKey.Down => "\x1b[B",
            TerminalKey.Home => control ? "\x1b[1;5H" : "\x1b[H",
            TerminalKey.End => control ? "\x1b[1;5F" : "\x1b[F",
            TerminalKey.PageUp => "\x1b[5~",
            TerminalKey.PageDown => "\x1b[6~",
            TerminalKey.Escape => "\x1b",
            _ => null,
        };
        if (sequence is null)
        {
            return false;
        }
        _ = WriteAsync(sequence);
        return true;
    }

    /// <summary>Paste sanitization: terminals submit CR, not LF or CRLF.</summary>
    public void Paste(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        SendText(text.Replace("\r\n", "\r").Replace("\n", "\r"));
    }

    public void Clear()
    {
        Buffer.Clear();
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Buffer.Cols && rows == Buffer.Rows)
        {
            return;
        }
        Buffer.Resize(cols, rows);
        if (IsConnected)
        {
            _service.Resize(cols, rows);
        }
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync(string text)
    {
        try
        {
            await _service.WriteAsync(text, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException is a subclass; either means the shell is gone.
        }
        catch (IOException)
        {
        }
    }

    private void OnOutputReceived(object? sender, string chunk)
    {
        _pendingOutput.Enqueue(chunk);
        if (Interlocked.Exchange(ref _outputScheduled, 1) == 0)
            _scheduler.Post(_drainOutput);
    }

    private void DrainOutput()
    {
        do
        {
            while (_pendingOutput.TryDequeue(out var chunk))
                Buffer.ApplyTokens(_parser.Feed(chunk));
            BufferChanged?.Invoke(this, EventArgs.Empty);
            Volatile.Write(ref _outputScheduled, 0);
        }
        while (!_pendingOutput.IsEmpty && Interlocked.CompareExchange(ref _outputScheduled, 1, 0) == 0);
    }

    private void OnExited(object? sender, int exitCode) => _scheduler.Post(() => IsConnected = false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _service.OutputReceived -= OnOutputReceived;
        _service.Exited -= OnExited;
        _service.Dispose();
    }
}
