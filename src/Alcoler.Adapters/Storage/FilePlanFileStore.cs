using System.Text;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Storage;

namespace Alcoler.Adapters.Storage;

public sealed class FilePlanFileStore : IPlanFileStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex UnsafeFileNameChars = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);

    private readonly string _root;

    public FilePlanFileStore(string root)
    {
        _root = root;
    }

    public async ValueTask<PlanFileReference> WriteAsync(
        SessionId sessionId,
        string plan,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = PathFor(sessionId);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, plan, Utf8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return new PlanFileReference(path, plan);
    }

    public async ValueTask<PlanFileReference?> ReadAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
            return null;
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(content) ? null : new PlanFileReference(path, content);
    }

    private string PathFor(SessionId sessionId)
    {
        var name = UnsafeFileNameChars.Replace(sessionId.Value.Trim(), "-").Trim('-');
        if (name.Length == 0)
            throw new ArgumentException("Session id cannot produce a plan file name", nameof(sessionId));
        return Path.Combine(_root, $"plan-{name}.md");
    }
}
