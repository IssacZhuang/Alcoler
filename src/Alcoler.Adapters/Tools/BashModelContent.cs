using System.Text;
using System.Text.RegularExpressions;

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
        return LeadingBlankLines().Replace(stdout, string.Empty).TrimEnd();
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
        var withoutTrailingNewlines = TrailingNewlines().Replace(stderr, string.Empty);
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

    public static string FormatPersistedOutputEnvelope(string content, long originalBytes, string persistedPath)
    {
        var preview = PreviewFirstChars(content, PersistedPreviewChars);
        var lines = new List<string>
        {
            "<persisted-output>",
            $"Output too large ({FormatByteSize(originalBytes)}). Full output saved to: {persistedPath}",
            string.Empty,
            $"Preview (first {FormatByteSize(PersistedPreviewChars)}):",
            preview.Preview,
        };
        if (preview.HasMore)
            lines.Add("...");
        lines.Add("</persisted-output>");
        return string.Join("\n", lines);
    }

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

    [GeneratedRegex(@"^(\s*\n)+")]
    private static partial Regex LeadingBlankLines();

    [GeneratedRegex(@"[\r\n]+$")]
    private static partial Regex TrailingNewlines();
}
