using System.Globalization;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using SkiaSharp;

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
        var offset = ToolInput.IntOrNull(context.Input, "offset") ?? 1;
        var startLine = Math.Max(offset, 1);
        var explicitLimit = ToolInput.IntOrNull(context.Input, "limit");
        if (context.FileReads?.FindView(path, offset, explicitLimit) is { CompleteView: true } snapshot
            && snapshot.MatchesCurrent(info.Length, info.LastWriteTimeUtc))
            return ToolExecutionResult.Text(FileUnchangedStub);

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
            context.FileReads?.RecordReadView(path, completeView: true, offset, explicitLimit,
                startLine == 1 && explicitLimit is null && lines.Length == 0 ? FileReadGuards.HashContent(content) : null);
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

        context.FileReads?.RecordReadView(path, completeView: true, offset, explicitLimit,
            startLine == 1 && explicitLimit is null && selectedEnd >= lines.Length ? FileReadGuards.HashContent(content) : null);
        return ToolExecutionResult.Text(AddLineNumbers(selected, offset == 0 ? 0 : startLine));
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

    private const int ReadImageMaxInputBytes = 20 * 1024 * 1024;
    private const int ReadImageMaxBase64Bytes = 5 * 1024 * 1024;
    private const int ReadImageMaxDimension = 2_000;
    private const double ImageTokenToBase64Ratio = 0.125;

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
        if (bytes.Length == 0)
            return ToolExecutionResult.Text("Image file is empty (0 bytes)", isError: true);
        try
        {
            (bytes, mimeType) = PrepareImage(bytes, mimeType, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }
        var fileName = mimeType == "image/jpeg" && ImageMimeFromPath(path) != mimeType
            ? Path.ChangeExtension(Path.GetFileName(path), ".jpg") : Path.GetFileName(path);
        var artifact = await context.Workspace.SaveArtifactAsync(
            context.SessionId, fileName, mimeType, bytes, cancellationToken).ConfigureAwait(false);
        return new ToolExecutionResult([new ToolResultMedia(artifact.Uri, artifact.MimeType, artifact.FileName)]);
    }

    private static (byte[] Bytes, string Mime) PrepareImage(byte[] input, string mime, CancellationToken token)
    {
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            throw new InvalidOperationException("Unable to decode image data");
        var info = codec.Info;
        mime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Gif => "image/gif",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => mime,
        };
        if (Math.Max(info.Width, info.Height) <= ReadImageMaxDimension && FitsImageBudget(input.Length))
            return (input, mime);
        var scale = Math.Min(1, ReadImageMaxDimension / (double)Math.Max(info.Width, info.Height));
        var desired = codec.GetScaledDimensions((float)scale);
        using var decoded = new SKBitmap(new SKImageInfo(desired.Width, desired.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidOperationException("Unable to decode image data");
        token.ThrowIfCancellationRequested();
        if (Math.Max(info.Width, info.Height) <= ReadImageMaxDimension && mime == "image/png")
        {
            using var original = SKImage.FromBitmap(decoded);
            using var png = original.Encode(SKEncodedImageFormat.Png, 100);
            if (png is not null && FitsImageBudget(png.Size))
                return (png.ToArray(), "image/png");
        }
        foreach (var edge in new[] { 2_000, 1_500, 1_000, 800, 600, 500, 400, 300, 200 })
        {
            token.ThrowIfCancellationRequested();
            var ratio = Math.Min(1, edge / (double)Math.Max(info.Width, info.Height));
            var width = Math.Max(1, (int)Math.Round(info.Width * ratio));
            var height = Math.Max(1, (int)Math.Round(info.Height * ratio));
            using var resized = decoded.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
            if (resized is null)
                throw new InvalidOperationException("Unable to resize image data");
            using var opaque = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(opaque))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(resized, 0, 0);
            }
            using var image = SKImage.FromBitmap(opaque);
            foreach (var quality in new[] { 80, 60, 40, 20 })
            {
                token.ThrowIfCancellationRequested();
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (encoded is not null && FitsImageBudget(encoded.Size))
                    return (encoded.ToArray(), "image/jpeg");
            }
        }
        throw new InvalidOperationException($"Unable to compress image ({input.Length} bytes) within the requested model image budget");
    }

    private static bool FitsImageBudget(long rawBytes)
    {
        var base64Bytes = ((rawBytes + 2) / 3) * 4;
        return rawBytes <= ReadImageMaxBase64Bytes * 3 / 4 && base64Bytes <= ReadImageMaxBase64Bytes
            && Math.Ceiling(base64Bytes * ImageTokenToBase64Ratio) <= MaxOutputTokens;
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
