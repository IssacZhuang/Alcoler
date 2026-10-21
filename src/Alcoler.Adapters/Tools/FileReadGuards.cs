using System.Security.Cryptography;
using System.Text;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

internal static class FileReadGuards
{
    private const string NotReadMessage = "File has not been read yet. Read it first before writing to it.";
    private const string StaleMessage =
        "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.";

    // Explicit ranges authorize edits; only tool-truncated views fail this guard.
    public static ToolExecutionResult? ReadStateFailure(string path, ToolExecutionContext context, string? currentContent = null)
    {
        var snapshot = context.FileReads?.Find(path);
        if (snapshot is null || !snapshot.CompleteView)
            return ToolExecutionResult.Text(NotReadMessage, isError: true);

        var info = new FileInfo(path);
        var changed = snapshot.LengthBytes != info.Length
            || new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() > snapshot.LastWriteUtc.ToUnixTimeMilliseconds();
        if (changed)
        {
            if (snapshot.Offset <= 1 && snapshot.Limit is null && snapshot.ContentHash is { } hash
                && HashContent(currentContent ?? File.ReadAllText(path)) == hash)
                return null;
            return ToolExecutionResult.Text(StaleMessage, isError: true);
        }
        return null;
    }

    internal static string HashContent(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
