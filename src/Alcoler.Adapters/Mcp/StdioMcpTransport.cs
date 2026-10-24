using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Adapters.Mcp;

internal sealed class StdioMcpTransport : IMcpTransport
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly Task _stderrPump;
    private readonly ILogger _logger;
    private readonly string _serverName;
    private int _disposed;

    public StdioMcpTransport(McpServerConfig config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? NullLogger.Instance;
        _serverName = config.Name;

        var startInfo = new ProcessStartInfo(config.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in config.Args ?? [])
            startInfo.ArgumentList.Add(argument);
        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
                startInfo.Environment[key] = value;
        }

        _process = new Process { StartInfo = startInfo };
        _process.Start();
        // MCP stdio requires UTF-8 without BOM; Process's own StandardInput encoding is not configurable.
        _stdin = new StreamWriter(
            _process.StandardInput.BaseStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096);
        _stderrPump = Task.Run(PumpStandardErrorAsync);
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(StdioMcpTransport));
        var line = ArrayPool<char>.Shared.Rent(message.Length + 1);
        try
        {
            message.CopyTo(line);
            line[message.Length] = '\n';
            try
            {
                await _stdin.WriteAsync(line.AsMemory(0, message.Length + 1), cancellationToken).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new IOException($"MCP server '{_serverName}' is no longer accepting input: {ex.Message}", ex);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(line);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(StdioMcpTransport));
        try
        {
            // BaseStream writes must stay ordered behind anything the StreamWriter still buffers.
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = ArrayPool<byte>.Shared.Rent(payload.Length + 1);
            try
            {
                payload.Span.CopyTo(line);
                line[payload.Length] = (byte)'\n';
                var stream = _stdin.BaseStream;
                await stream.WriteAsync(line.AsMemory(0, payload.Length + 1), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException($"MCP server '{_serverName}' is no longer accepting input: {ex.Message}", ex);
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            await _stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        _stdin.Dispose();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ShutdownGrace)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception)
        {
        }
        _process.Dispose();
        try
        {
            await _stderrPump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                _logger.LogInformation("MCP server '{ServerName}' stderr: {Line}", _serverName, line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
