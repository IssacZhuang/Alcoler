using System.Text;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Assembles the model-visible Bash result: exit-code header, stdout/stderr normalization,
/// persisted-output envelope for oversized output, and background start announcements.
/// All strings are ported from ZCode's bash-model-content / result-persistence-format.
/// </summary>
public static partial class BashModelContent
{
    public const int MaxInlineOutputBytes = 30_000;
    public const int PersistedPreviewChars = 2_000;

    public static string FormatStdout(string stdout)
    {
        if (stdout.Length == 0)
            return string.Empty;
        // The regex strips the longest leading whitespace prefix ending in a newline,
        // leaving whitespace on the first non-blank line intact.
        var lastNewline = -1;
        for (var index = 0; index < stdout.Length; index++)
        {
            if (!char.IsWhiteSpace(stdout[index]))
                break;
            if (stdout[index] == '\n')
                lastNewline = index;
        }
        return stdout.AsSpan(lastNewline + 1).TrimEnd().ToString();
    }

    public static string FormatStderr(string stderr, bool interrupted)
    {
        var message = stderr.Trim();
        if (interrupted)
        {
            if (message.Length > 0)
                message += "\n";
            message += "<error>Command was aborted before completion</error>";
        }
        return message;
    }

    public static string AppendCwdStderrSuffix(string stderr, string? suffix)
    {
        if (suffix is null)
            return stderr;
        var end = stderr.Length;
        while (end > 0 && stderr[end - 1] is '\r' or '\n')
            end--;
        var withoutTrailingNewlines = end == stderr.Length ? stderr : stderr[..end];
        if (withoutTrailingNewlines.Length == 0)
            return suffix;
        return withoutTrailingNewlines + "\n" + suffix;
    }

    public static string FormatProviderError(int exitCode, string stdout, string stderr) =>
        new[] { $"Exit code {exitCode}", stdout, stderr }.Where(static part => part.Length > 0)
            .Aggregate(static (a, b) => a + "\n" + b);

    public static string FormatBackgroundStart(string taskId, string outputPath) =>
        $"Command running in background with ID: {taskId}. Output is being written to: {outputPath}. "
        + "You will be notified when it completes. To check interim output, use Read on that file path.";

    public static string FormatAutoBackgrounded(string taskId, string outputPath, int blockingBudgetMs) =>
        $"Command exceeded the blocking budget ({blockingBudgetMs / 1000.0:0.##}s) and was moved to the background "
        + $"with ID: {taskId}. It is still running — you will be notified when it completes. "
        + $"Output is being written to: {outputPath}. To check interim output, use Read on that file path. "
        + "Delegate long-running work to a subagent or use run_in_background to keep this conversation responsive.";

    public static string FormatStaleReadHint(IReadOnlyList<string> paths, string workingDirectory)
    {
        const int maxDisplayPaths = 5;
        var displayPaths = paths.Take(maxDisplayPaths)
            .Select(path => Path.GetRelativePath(workingDirectory, path) is { Length: > 0 } relative ? relative : path)
            .ToList();
        var hiddenCount = paths.Count - maxDisplayPaths;
        var hiddenSuffix = hiddenCount > 0 ? $" and {hiddenCount} more" : string.Empty;
        var fileWord = paths.Count == 1 ? "file" : "files";
        return $"[This command modified {paths.Count} {fileWord} you've previously read: "
            + $"{string.Join(", ", displayPaths)}{hiddenSuffix}. Call Read before editing.]";
    }

    public static string FormatPersistedOutputEnvelope(string content, long originalBytes, string persistedPath, bool decimalBytes = false)
    {
        var preview = PreviewFirstChars(content, PersistedPreviewChars);
        Func<long, string> formatBytes = decimalBytes ? FormatDecimalBytes : FormatByteSize;
        var lines = new List<string>
        {
            "<persisted-output>",
            $"Output too large ({formatBytes(originalBytes)}). Full output saved to: {persistedPath}",
            string.Empty,
            $"Preview (first {formatBytes(PersistedPreviewChars)}):",
            preview.Preview,
        };
        if (preview.HasMore)
            lines.Add("...");
        lines.Add("</persisted-output>");
        return string.Join("\n", lines);
    }

    internal static string FormatPersistenceFailure(string tail, long originalBytes)
    {
        var suffix = $"\n\n[Tool output truncated by resultBudget: originalBytes={originalBytes}, maxModelBytes={MaxInlineOutputBytes}, strategy=artifact]";
        var remaining = MaxInlineOutputBytes - Encoding.UTF8.GetByteCount(suffix);
        var bytes = Encoding.UTF8.GetBytes(tail);
        var start = Math.Max(0, bytes.Length - remaining);
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80)
            start++;
        return Encoding.UTF8.GetString(bytes.AsSpan(start)) + suffix;
    }

    private static string FormatDecimalBytes(long bytes) => bytes switch
    {
        < 1_000 => $"{bytes} B",
        < 1_000_000 => $"{Math.Round(bytes / 1_000.0, MidpointRounding.AwayFromZero)} KB",
        < 1_000_000_000 => $"{Math.Round(bytes / 1_000_000.0, MidpointRounding.AwayFromZero)} MB",
        _ => $"{Math.Round(bytes / 1_000_000_000.0, MidpointRounding.AwayFromZero)} GB",
    };

    public static string FormatByteSize(long bytes)
    {
        if (bytes <= 0)
            return "0 bytes";
        var kb = bytes / 1024.0;
        if (kb < 1)
            return $"{bytes} bytes";
        if (kb < 1024)
            return $"{TrimUnit(kb)}KB";
        var mb = kb / 1024;
        if (mb < 1024)
            return $"{TrimUnit(mb)}MB";
        return $"{TrimUnit(mb / 1024)}GB";
    }

    private static string TrimUnit(double value)
    {
        var formatted = Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0");
        return formatted.EndsWith(".0", StringComparison.Ordinal) ? formatted[..^2] : formatted;
    }

    private static (string Preview, bool HasMore) PreviewFirstChars(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return (content, false);
        var head = content[..maxChars];
        var newlineIndex = head.LastIndexOf('\n');
        var endIndex = newlineIndex > maxChars * 0.5 ? newlineIndex : maxChars;
        return (content[..endIndex], true);
    }
}
