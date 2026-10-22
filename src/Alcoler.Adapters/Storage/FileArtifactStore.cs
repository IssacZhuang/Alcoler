using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Storage;

public sealed class FileArtifactStore : IArtifactStore
{
    public const string UriScheme = "alcoler-artifact";

    private readonly string _root;

    public FileArtifactStore(string artifactsRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRootDirectory);
        _root = Path.GetFullPath(artifactsRootDirectory);
    }

    public async ValueTask<ArtifactReference> SaveAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var session = ValidateSegment(sessionId.Value, nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        Span<char> guidChars = stackalloc char[32];
        Guid.NewGuid().TryFormat(guidChars, out _, "N");
        var storedName = $"{new string(guidChars[..8])}-{SanitizeFileName(fileName)}";
        var directory = Path.Combine(_root, session);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, storedName), data, cancellationToken)
            .ConfigureAwait(false);
        return new ArtifactReference($"{UriScheme}://{session}/{storedName}", mimeType, fileName);
    }

    public async ValueTask<ReadOnlyMemory<byte>?> TryLoadAsync(
        string artifactUri,
        CancellationToken cancellationToken)
    {
        if (!TryParseArtifactUri(artifactUri, out var session, out var fileName))
        {
            return null;
        }

        var filePath = Path.GetFullPath(Path.Combine(_root, session, fileName));
        if (!PathGuard.IsUnderRoot(filePath, _root) || !File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DeleteSessionArtifactsAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        var session = ValidateSegment(sessionId.Value, nameof(sessionId));
        var directory = Path.GetFullPath(Path.Combine(_root, session));
        if (!PathGuard.IsUnderRoot(directory, _root) || !Directory.Exists(directory))
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    // "scheme://host/path" parsing would swallow the session id into the authority, so the reference format is parsed textually.
    private static bool TryParseArtifactUri(string artifactUri, out string session, out string fileName)
    {
        session = string.Empty;
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(artifactUri))
        {
            return false;
        }

        var prefix = UriScheme + "://";
        if (!artifactUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = artifactUri[prefix.Length..];
        var separator = remainder.IndexOf('/');
        if (separator <= 0 || separator == remainder.Length - 1)
        {
            return false;
        }

        session = remainder[..separator];
        fileName = remainder[(separator + 1)..];
        return IsSafeSegment(session)
            && IsSafeSegment(fileName)
            && fileName.IndexOfAny(['/', '\\']) < 0;
    }

    private static string ValidateSegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsSafeSegment(value))
        {
            throw new ArgumentException("Value must be a single safe path segment.", paramName);
        }

        return value;
    }

    private static bool IsSafeSegment(string segment) =>
        segment is not ("." or "..") && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[fileName.Length];
        for (var index = 0; index < fileName.Length; index++)
            sanitized[index] = Array.IndexOf(invalid, fileName[index]) >= 0 ? '_' : fileName[index];
        var length = sanitized.Length;
        while (length > 0 && sanitized[length - 1] is ' ' or '.')
            length--;
        return length > 0 ? new string(sanitized, 0, length) : "artifact";
    }
}
