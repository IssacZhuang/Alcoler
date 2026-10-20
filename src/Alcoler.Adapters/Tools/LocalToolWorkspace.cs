using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class LocalToolWorkspace : IToolWorkspace
{
    private readonly string _root;
    private readonly StringComparison _pathComparison;

    public LocalToolWorkspace(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public string Root => _root;

    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        var rootPrefix = _root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, _pathComparison) && !string.Equals(fullPath, _root, _pathComparison))
            throw new InvalidOperationException($"Path '{path}' escapes the workspace root '{_root}'.");
        return fullPath;
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
