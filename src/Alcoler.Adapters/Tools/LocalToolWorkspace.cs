using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class LocalToolWorkspace : IToolWorkspace
{
    private readonly string _root;

    public LocalToolWorkspace(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root => _root;

    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Paths outside _root are intentionally allowed so tools can inspect sibling repositories
        // and user-requested external files.
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
    }

    public async ValueTask<ArtifactReference> SaveArtifactAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_root, ".alcoler", "artifacts", sessionId.Value);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(filePath, data, cancellationToken);
        return new ArtifactReference($"alcoler-artifact://{sessionId.Value}/{fileName}", mimeType, fileName);
    }
}
