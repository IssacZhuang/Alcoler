using System.Text;
using System.Threading.Channels;

namespace Alcoler.Adapters.Mcp;

internal static class MemoryMcpTransport
{
    public static (IMcpTransport Client, IMcpTransport Server) CreatePair()
    {
        var toServer = Channel.CreateUnbounded<string>(Options);
        var toClient = Channel.CreateUnbounded<string>(Options);
        return (
            new ChannelMcpTransport("client", inbox: toClient, outbox: toServer),
            new ChannelMcpTransport("server", inbox: toServer, outbox: toClient));
    }

    private static UnboundedChannelOptions Options { get; } = new() { SingleReader = true, SingleWriter = true };

    private sealed class ChannelMcpTransport(string name, Channel<string> inbox, Channel<string> outbox) : IMcpTransport
    {
        private int _disposed;

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException($"memory-mcp-transport-{name}");
            if (outbox.Writer.TryWrite(message))
                return Task.CompletedTask;
            throw new IOException($"The memory transport '{name}' can no longer send because the peer was disposed.");
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            SendAsync(Encoding.UTF8.GetString(payload.Span), cancellationToken);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;
            inbox.Writer.TryComplete();
            outbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
