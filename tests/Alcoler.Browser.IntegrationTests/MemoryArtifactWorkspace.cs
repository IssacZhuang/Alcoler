using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Browser.IntegrationTests;

internal sealed class MemoryArtifactWorkspace : IToolWorkspace
{
    public string Root => "memory://native-browser-integration/";
    public List<SavedArtifact> Artifacts { get; } = [];

    public string ResolvePath(string path) => throw new NotSupportedException("The integration workspace stores artifacts only in memory.");

    public ValueTask<ArtifactReference> SaveArtifactAsync(SessionId sessionId, string fileName, string mimeType,
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = Root + Guid.NewGuid().ToString("N") + "/" + Uri.EscapeDataString(fileName);
        Artifacts.Add(new(sessionId, uri, fileName, mimeType, data.ToArray()));
        return ValueTask.FromResult(new ArtifactReference(uri, mimeType, fileName));
    }

    internal sealed record SavedArtifact(SessionId SessionId, string Uri, string FileName, string MimeType, byte[] Data);
}
