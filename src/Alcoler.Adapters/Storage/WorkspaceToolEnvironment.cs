using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Storage;

public sealed class WorkspaceToolEnvironment : IToolWorkspace
{
    private readonly IArtifactStore _artifactStore;

    public WorkspaceToolEnvironment(string workspaceRoot, IArtifactStore artifactStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(artifactStore);
        Root = Path.GetFullPath(workspaceRoot);
        _artifactStore = artifactStore;
    }

    public string Root { get; }

    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty or whitespace.", nameof(path));
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException($"Path contains invalid characters: '{path}'.", nameof(path));
        }

        var candidate = Path.GetFullPath(Path.Combine(Root, path));
        if (!PathGuard.IsUnderRoot(candidate, Root))
        {
            throw new InvalidOperationException(
                $"Path '{path}' resolves to '{candidate}', outside the workspace root '{Root}'.");
        }

        return candidate;
    }

    public ValueTask<ArtifactReference> SaveArtifactAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) =>
        _artifactStore.SaveAsync(sessionId, fileName, mimeType, data, cancellationToken);
}
