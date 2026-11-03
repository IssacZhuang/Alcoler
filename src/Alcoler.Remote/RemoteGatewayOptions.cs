using Alcoler.Contracts;

namespace Alcoler.Remote;

public sealed class RemoteGatewayOptions
{
    public required Func<Contracts.Projections.IAgentRuntime> Runtime { get; init; }
    public int Port { get; init; } = 8757;
    public string? DataDirectory { get; init; }
    public Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>>? ArtifactLoader { get; init; }
    public string? ProductVersion { get; init; }
    public bool AllowNonPrivateClients { get; init; }
    public string? ConnectionCode { get; init; }
}
