using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

internal static class FileReadGuards
{
    private const string NotReadMessage = "File has not been read yet. Read it first before writing to it.";
    private const string StaleMessage =
        "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.";

    // Modifying an existing file demands a complete, still-current read; an incomplete view
    // leaves parts of the file the model has never seen.
    public static ToolExecutionResult? ReadStateFailure(string path, ToolExecutionContext context)
    {
        var snapshot = context.FileReads?.Find(path);
        if (snapshot is null || !snapshot.CompleteView)
            return ToolExecutionResult.Text(NotReadMessage, isError: true);

        var info = new FileInfo(path);
        if (!snapshot.MatchesCurrent(info.Length, info.LastWriteTimeUtc))
            return ToolExecutionResult.Text(StaleMessage, isError: true);
        return null;
    }
}
