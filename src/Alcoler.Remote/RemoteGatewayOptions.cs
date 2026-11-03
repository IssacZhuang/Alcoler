using Alcoler.Contracts;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Remote;

public sealed record RemoteModelCatalog(
    IReadOnlyList<RemoteModelChoice> Models,
    ModelSelection? DefaultSelection);

public sealed record RemoteModelChoice(
    string ProviderId,
    string ProviderLabel,
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> ReasoningLevels);

public sealed record RemoteSlashEntry(string Name, string Description, string Kind);

public sealed record RemoteSlashCatalog(IReadOnlyList<RemoteSlashEntry> Entries);

public sealed record ArtifactThumbnail(ReadOnlyMemory<byte> Bytes, string MimeType);

public sealed class RemoteGatewayOptions
{
    public required Func<Contracts.Projections.IAgentRuntime> Runtime { get; init; }
    public int Port { get; init; } = 8757;
    public string? DataDirectory { get; init; }
    public Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>>? ArtifactLoader { get; init; }
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ArtifactThumbnail?>>? ArtifactThumbnailGenerator { get; init; }
    public string? ProductVersion { get; init; }
    public Func<RemoteModelCatalog>? ModelCatalog { get; init; }
    public Func<SessionId?, string?, CancellationToken, ValueTask<RemoteSlashCatalog?>>? SlashCatalog { get; init; }
    public bool AllowNonPrivateClients { get; init; }
    public string? ConnectionCode { get; init; }
}
