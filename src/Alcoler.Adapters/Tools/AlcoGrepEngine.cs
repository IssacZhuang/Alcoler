using Alcoler.Adapters.Grep;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Primary engine: in-process ripgrep via the alco-grep native library
/// (ignore-file rules, parallel walk, binary/BOM handling match rg).
/// </summary>
internal sealed class AlcoGrepEngine : IGrepEngine
{
    public static AlcoGrepEngine? TryCreate() => AlcoGrepNative.IsAvailable ? new() : null;

    public GrepEngineResult Search(GrepEngineRequest request, CancellationToken cancellationToken)
    {
        var globs = new List<string>(request.GlobPatterns);
        if (request.TypeFilter is { } type)
            globs.AddRange(GrepTypeMap.ToGlobs(type));
        var mode = request.Mode switch
        {
            "content" => AlcoGrepAbi.ModeContent,
            "count" => AlcoGrepAbi.ModeCount,
            _ => AlcoGrepAbi.ModeFilesWithMatches,
        };
        var query = new AlcoGrepQuery(
            request.Pattern,
            request.SearchPath,
            globs,
            mode,
            request.IgnoreCase,
            request.Multiline,
            request.OnlyMatching,
            (uint)Math.Max(0, request.BeforeContext),
            (uint)Math.Max(0, request.AfterContext));
        return Assemble(AlcoGrepNative.Search(query, cancellationToken));
    }

    private static GrepEngineResult Assemble(AlcoGrepRawResult raw)
    {
        var result = new GrepEngineResult();
        foreach (var (kind, path, lineNumber, text, count) in raw.Events)
        {
            switch (kind)
            {
                case AlcoGrepAbi.EventMatch:
                    result.ContentEntries.Add(new GrepContentEntry(path, lineNumber > 0 ? (int)lineNumber : null, text));
                    break;
                case AlcoGrepAbi.EventContext:
                    result.ContentEntries.Add(new GrepContentEntry(path, (int)lineNumber, text));
                    break;
                case AlcoGrepAbi.EventFileCount:
                    result.MatchedFiles.Add(path);
                    result.CountEntries.Add((path, (long)count));
                    break;
            }
        }
        return result;
    }
}
