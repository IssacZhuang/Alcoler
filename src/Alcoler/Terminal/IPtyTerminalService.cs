namespace Alcoler.Terminal;

/// <summary>
/// A shell process attached to a pseudoterminal. Output chunks are raised on a
/// background thread; consumers must marshal before touching UI state.
/// </summary>
public interface IPtyTerminalService : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Decoded UTF-8 output as received from the PTY; chunk boundaries are arbitrary.</summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Raised once when the shell process exits; carries its exit code.</summary>
    event EventHandler<int>? Exited;

    Task StartAsync(string workingDirectory, int cols, int rows, CancellationToken cancellationToken);

    Task WriteAsync(string text, CancellationToken cancellationToken);

    void Resize(int cols, int rows);

    /// <summary>Requests process exit and waits briefly for the PTY to drain.</summary>
    Task StopAsync();
}
