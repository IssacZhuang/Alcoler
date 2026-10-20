using System.Globalization;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;

namespace Alcoler.Adapters.Tools;

public sealed class ReadTool : ITool
{
    private const int DefaultMaxLines = 2_000;
    private const int MaxFileSizeBytes = 256 * 1024;
    private const int MaxOutputTokens = 25_000;
    // 85% of the output budget, matching the reference partial-view shrink target.
    private static readonly int PartialViewTokenTarget = (int)Math.Floor(MaxOutputTokens * 0.85);

    private const string FileUnchangedStub =
        "Wasted call \u2014 file unchanged since your last Read. Refer to that earlier tool_result instead.";
    private const string EmptyFileReminder =
        "<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>";

    private static readonly HashSet<string> UnsupportedBinaryExtensions = new(StringComparer.Ordinal)
    {
        ".7z", ".a", ".bin", ".bz2", ".class", ".dll", ".dmg", ".dylib", ".exe",
        ".gz", ".jar", ".o", ".pyc", ".rar", ".so", ".tar", ".tgz", ".wasm", ".zip",
    };

    public ToolContract Contract { get; } = new(
        "Read",
        """
        Reads a file from the local filesystem.

        - `file_path` must be an absolute path.
        - Reads up to 2000 lines by default.
        - You can optionally specify a line offset and limit (especially handy for long files), but it's recommended to read the whole file by not providing these parameters
        - Results are returned using cat -n format, with line numbers starting at 1
        - Reads images (PNG, JPG, …) and presents them visually.
        - Reading a directory, a missing file, or an empty file returns an error or system reminder rather than content.
        - Do NOT re-read a file you just edited to verify — Edit/Write would have errored if the change failed, and the harness tracks file state for you.
        """,
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "The absolute path to the file to read" },
            "offset": { "type": "integer", "minimum": 0, "description": "The line number to start reading from. Only provide if the file is too large to read at once" },
            "limit": { "type": "integer", "minimum": 1, "description": "The number of lines to read. Only provide if the file is too large to read at once." }
          },
          "required": ["file_path"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolInput.String(context.Input, "file_path");
        string path;
        try
        {
            path = context.Workspace.ResolvePath(rawPath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        if (ImageMimeFromPath(path) is { } imageMime)
            return await ReadImageAsync(context, path, imageMime, cancellationToken).ConfigureAwait(false);

        if (BinaryExtension(path) is { } binaryExtension)
            return ToolExecutionResult.Text(
                $"This tool cannot read binary files. The file appears to be a binary {binaryExtension} file. Please use appropriate tools for binary file analysis.",
                isError: true);

        if (!File.Exists(path))
            return ToolExecutionResult.Text(
                MissingFileSuggestions.CreateMissingFileMessage(path, CurrentDirectory(context)),
                isError: true);

        var info = new FileInfo(path);
        if (context.FileReads?.Find(path) is { CompleteView: true } snapshot
            && snapshot.MatchesCurrent(info.Length, info.LastWriteTimeUtc))
            return ToolExecutionResult.Text(FileUnchangedStub);

        var startLine = Math.Max(ToolInput.IntOrNull(context.Input, "offset") ?? 1, 1);
        var explicitLimit = ToolInput.IntOrNull(context.Input, "limit");
        var limit = explicitLimit ?? DefaultMaxLines;

        if (explicitLimit is null && info.Length > MaxFileSizeBytes)
            return ToolExecutionResult.Text(
                $"File content ({FormatFileSize(info.Length)}) exceeds maximum allowed size ({FormatFileSize(MaxFileSizeBytes)}). Use offset and limit parameters to read specific portions of the file, or search for specific content instead of reading the whole file.",
                isError: true);

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lines = content.Length == 0 ? [] : EditMatchers.NormalizeLineEndings(content).Split('\n');
        var selectedEnd = (int)Math.Min((long)startLine - 1 + limit, lines.Length);
        var selected = startLine - 1 < lines.Length ? lines[(startLine - 1)..selectedEnd] : [];

        if (selected.Length == 0)
        {
            var warning = lines.Length == 0
                ? EmptyFileReminder
                : $"<system-reminder>Warning: the file exists but is shorter than the provided offset ({startLine}). The file has {lines.Length} lines.</system-reminder>";
            context.FileReads?.RecordRead(path, completeView: lines.Length == 0);
            return ToolExecutionResult.Text(warning);
        }

        var tokenCount = TokenEstimator.EstimateTokens(string.Join("\n", selected));
        if (tokenCount > MaxOutputTokens)
        {
            // The token-cap partial view is reserved for initial whole-file reads; explicit
            // ranges must error so the model narrows its own request.
            if (startLine > 1 || explicitLimit is not null)
                return ToolExecutionResult.Text(
                    $"File content ({tokenCount} tokens) exceeds maximum allowed tokens ({MaxOutputTokens}). Use offset and limit parameters to read specific portions of the file, or search for specific content instead of reading the whole file.",
                    isError: true);
            return RenderPartialView(context, path, lines, selected, tokenCount);
        }

        context.FileReads?.RecordRead(path, completeView: startLine == 1 && selectedEnd >= lines.Length);
        return ToolExecutionResult.Text(AddLineNumbers(selected, startLine));
    }

    private ToolExecutionResult RenderPartialView(
        ToolExecutionContext context,
        string path,
        string[] lines,
        string[] selected,
        int tokenCount)
    {
        string notice;
        string numbered;
        if (LargestPrefixWithinTokenBudget(selected) is > 0 and var lineCount)
        {
            notice = $"The file is too large to display in full ({tokenCount} estimated tokens, limit {MaxOutputTokens}). "
                + $"Showing a partial view of lines 1-{lineCount} of {lines.Length}. "
                + $"Use Read with offset {lineCount + 1} and limit {DefaultMaxLines} to continue, or use a search tool to find a specific section.";
            numbered = AddLineNumbers(selected[..lineCount], 1);
        }
        else
        {
            var content = string.Join("\n", selected);
            var charCount = LargestCharPrefixWithinTokenBudget(content);
            if (charCount <= 0)
                return ToolExecutionResult.Text(
                    $"File content ({tokenCount} tokens) exceeds maximum allowed tokens ({MaxOutputTokens}). Use offset and limit parameters to read specific portions of the file, or search for specific content instead of reading the whole file.",
                    isError: true);
            notice = $"The file is too large to display in full ({tokenCount} estimated tokens, limit {MaxOutputTokens}). "
                + "Showing a partial view of the first line because the first line alone exceeds the token budget. "
                + "Use Read with a smaller range or use a search tool to find a specific section.";
            numbered = AddLineNumbers([content[..charCount]], 1);
        }

        context.FileReads?.RecordRead(path, completeView: false);
        return ToolExecutionResult.Text($"<system-reminder>{notice}</system-reminder>\n\n{numbered}");
    }

    private static string CurrentDirectory(ToolExecutionContext context) =>
        context.Shell?.CurrentDirectory ?? context.WorkspaceRoot;

    // Mirrors ZCode READ_IMAGE_* budgets. Without an image transcoder, images that the
    // reference would resize or compress are rejected instead of transformed.
    private const int ReadImageMaxInputBytes = 20 * 1024 * 1024;
    private const int ReadImageMaxBase64Bytes = 5 * 1024 * 1024;

    private static async ValueTask<ToolExecutionResult> ReadImageAsync(
        ToolExecutionContext context, string path, string mimeType, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return ToolExecutionResult.Text(
                MissingFileSuggestions.CreateMissingFileMessage(path, CurrentDirectory(context)), isError: true);

        var info = new FileInfo(path);
        if (info.Length > ReadImageMaxInputBytes)
            return ToolExecutionResult.Text(
                $"Image file is too large ({FormatFileSize(info.Length)}). Image reads are capped at {FormatFileSize(ReadImageMaxInputBytes)}.",
                isError: true);

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var base64Length = (long)Math.Ceiling(bytes.Length / 3.0) * 4;
        if (base64Length > ReadImageMaxBase64Bytes)
            return ToolExecutionResult.Text(
                $"Image is too large for the model request budget ({FormatFileSize(bytes.Length)} raw, {FormatFileSize((int)Math.Min(int.MaxValue, base64Length))} encoded; the cap is {FormatFileSize(ReadImageMaxBase64Bytes)} encoded). Downscale or recompress the image, then read it again.",
                isError: true);

        var artifact = await context.Workspace.SaveArtifactAsync(
            context.SessionId, Path.GetFileName(path), mimeType, bytes, cancellationToken).ConfigureAwait(false);
        return new ToolExecutionResult([new ToolResultMedia(artifact.Uri, artifact.MimeType, artifact.FileName)]);
    }

    private static string? ImageMimeFromPath(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) return "image/jpeg";
        if (lower.EndsWith(".png")) return "image/png";
        if (lower.EndsWith(".gif")) return "image/gif";
        if (lower.EndsWith(".webp")) return "image/webp";
        return null;
    }

    private static string? BinaryExtension(string path)
    {
        var name = Path.GetFileName(path);
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex <= 0)
            return null;
        var extension = name[dotIndex..].ToLowerInvariant();
        return UnsupportedBinaryExtensions.Contains(extension) ? extension : null;
    }

    private static string AddLineNumbers(string[] lines, int startLine) =>
        string.Join("\n", lines.Select((line, index) => $"{startLine + index}\t{line}"));

    private static int LargestPrefixWithinTokenBudget(string[] lines)
    {
        var low = 0;
        var high = lines.Length;
        while (low < high)
        {
            var mid = (int)Math.Ceiling((low + (double)high) / 2);
            if (TokenEstimator.EstimateTokens(string.Join("\n", lines, 0, mid)) <= PartialViewTokenTarget)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    private static int LargestCharPrefixWithinTokenBudget(string content)
    {
        var low = 0;
        var high = content.Length;
        while (low < high)
        {
            var mid = (int)Math.Ceiling((low + (double)high) / 2);
            if (TokenEstimator.EstimateTokens(content[..mid]) <= PartialViewTokenTarget)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes}B";
        if (bytes < 1024 * 1024)
            return $"{FormatUnit(bytes / 1024.0)}KB";
        return $"{FormatUnit(bytes / (1024.0 * 1024))}MB";
    }

    private static string FormatUnit(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
