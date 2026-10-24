namespace Alcoler.Adapters.Mcp;

// A message is one complete JSON document; ReceiveAsync returns null at end of stream.
internal interface IMcpTransport : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}
