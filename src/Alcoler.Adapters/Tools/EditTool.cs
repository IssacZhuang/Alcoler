using System.Text.Json;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class EditTool : ITool
{
    private const string FreshnessSuffix = " (file state is current in your context \u2014 no need to Read it back)";
    private const long MaxEditableFileSizeBytes = 1024L * 1024 * 1024;

    public ToolContract Contract { get; } = new(
        "Edit",
        """
        Performs exact string replacement in a file.

        - You must Read the file in this conversation before editing, or the call will fail.
        - `old_string` must match the file exactly, including indentation, and be unique — the edit fails otherwise. Strip the Read line prefix (line number + tab) before matching.
        - `replace_all: true` replaces every occurrence instead.
        """,
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "The absolute path to the file to modify" },
            "old_string": { "type": "string", "description": "The text to replace" },
            "new_string": { "type": "string", "description": "The text to replace it with (must be different from old_string)" },
            "replace_all": { "type": "boolean", "default": false, "description": "Replace all occurrences of old_string (default false)" }
          },
          "required": ["file_path", "old_string", "new_string"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Workspace,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolInput.String(context.Input, "file_path");
        var oldStringInput = ToolInput.String(context.Input, "old_string");
        var newStringInput = ToolInput.String(context.Input, "new_string");
        var replaceAll = ParseReplaceAll(context.Input);

        if (oldStringInput == newStringInput)
            return ToolExecutionResult.Text(
                "No changes to make: old_string and new_string are exactly the same.",
                isError: true);

        if (rawPath.Length == 0)
            return ToolExecutionResult.Text("Tool path must not be empty", isError: true);

        string path;
        try
        {
            path = context.Workspace.ResolvePath(rawPath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        if (!File.Exists(path))
        {
            if (oldStringInput.Length == 0)
                return await CreateMissingFileAsync(context, path, rawPath, newStringInput, replaceAll, cancellationToken);
            return ToolExecutionResult.Text(
                MissingFileSuggestions.CreateMissingFileMessage(path, CurrentDirectory(context)),
                isError: true);
        }

        if (new FileInfo(path).Length > MaxEditableFileSizeBytes)
            return ToolExecutionResult.Text(
                "File is too large to edit (1GB). Maximum editable file size is 1GB.",
                isError: true);

        var rawContent = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var content = EditMatchers.NormalizeLineEndings(rawContent);
        var oldString = EditMatchers.NormalizeLineEndings(oldStringInput);
        var requestedNewString = EditMatchers.NormalizeLineEndings(newStringInput);

        if (oldString.Length == 0)
        {
            if (content.Trim().Length != 0)
                return ToolExecutionResult.Text("Cannot create new file - file already exists.", isError: true);
            if (FileReadGuards.ReadStateFailure(path, context) is { } createFailure)
                return createFailure;
            await WritePreservingLineEndingsAsync(path, rawContent, requestedNewString, cancellationToken).ConfigureAwait(false);
            context.FileReads?.RecordWrite(path);
            return Success(rawPath, replaceAll);
        }

        if (path.EndsWith(".ipynb", StringComparison.Ordinal))
            return ToolExecutionResult.Text(
                "File is a Jupyter Notebook. Use the NotebookEdit to edit this file.",
                isError: true);

        if (FileReadGuards.ReadStateFailure(path, context) is { } failure)
            return failure;

        var match = EditMatchers.FindEditMatch(content, oldString, replaceAll);
        if (match is EditMatch.NotFound)
            return ToolExecutionResult.Text(
                $"String to replace not found in file.\nString: {oldStringInput}",
                isError: true);
        if (match is EditMatch.Ambiguous ambiguous)
            return ToolExecutionResult.Text(AmbiguousMessage(ambiguous.CandidateCount, oldStringInput), isError: true);
        var matched = (EditMatch.Matched)match;

        var actualOldString = matched.ActualString;
        if (!replaceAll && CountOccurrences(content, actualOldString) is > 1 and var matchCount)
            return ToolExecutionResult.Text(AmbiguousMessage(matchCount, oldStringInput), isError: true);

        var normalizedNewString = EditMatchers.NormalizeReplacementForMatch(matched.Strategy, requestedNewString);
        var actualNewString = EditMatchers.PreserveQuoteStyle(oldString, actualOldString, normalizedNewString);
        var newContent = ApplyEdit(content, actualOldString, actualNewString, replaceAll);
        await WritePreservingLineEndingsAsync(path, rawContent, newContent, cancellationToken).ConfigureAwait(false);
        context.FileReads?.RecordWrite(path);
        return Success(rawPath, replaceAll);
    }

    private static ToolExecutionResult Success(string filePath, bool replaceAll) =>
        ToolExecutionResult.Text(replaceAll
            ? $"The file {filePath} has been updated. All occurrences were successfully replaced.{FreshnessSuffix}"
            : $"The file {filePath} has been updated successfully.{FreshnessSuffix}");

    private static string CurrentDirectory(ToolExecutionContext context) =>
        context.Shell?.CurrentDirectory ?? context.WorkspaceRoot;

    private static bool ParseReplaceAll(JsonElement input)
    {
        if (!input.TryGetProperty("replace_all", out var value))
            return false;
        if (value.ValueKind is JsonValueKind.True)
            return true;
        if (value.ValueKind is JsonValueKind.Number)
            return value.TryGetInt32(out var number) && number == 1;
        if (value.ValueKind is JsonValueKind.String)
            return value.GetString()!.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "y" or "on";
        return false;
    }

    private static async ValueTask<ToolExecutionResult> CreateMissingFileAsync(
        ToolExecutionContext context,
        string path,
        string rawPath,
        string newString,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, newString, cancellationToken).ConfigureAwait(false);
        context.FileReads?.RecordWrite(path);
        return Success(rawPath, replaceAll);
    }

    private static string AmbiguousMessage(int matchCount, string oldString) =>
        $"Found {matchCount} matches of the string to replace, but replace_all is false. "
        + "To replace all occurrences, set replace_all to true. "
        + "To replace only one occurrence, please provide more context to uniquely identify the instance.\n"
        + $"String: {oldString}";

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var position = 0;
        while (position <= content.Length)
        {
            var index = content.IndexOf(needle, position, StringComparison.Ordinal);
            if (index < 0)
                break;
            count++;
            position = index + Math.Max(needle.Length, 1);
        }
        return count;
    }

    private static string ApplyEdit(string content, string oldString, string newString, bool replaceAll)
    {
        if (newString.Length != 0)
            return ReplaceLiteral(content, oldString, newString, replaceAll);

        // Deleting with an empty replacement swallows the trailing newline so whole-line
        // removals do not leave blank lines behind.
        var search = !oldString.EndsWith('\n') && content.Contains(oldString + "\n", StringComparison.Ordinal)
            ? oldString + "\n"
            : oldString;
        return ReplaceLiteral(content, search, newString, replaceAll);
    }

    private static string ReplaceLiteral(string content, string search, string replacement, bool replaceAll)
    {
        if (replaceAll)
            return content.Replace(search, replacement, StringComparison.Ordinal);
        var index = content.IndexOf(search, StringComparison.Ordinal);
        return string.Concat(content.AsSpan(0, index), replacement, content.AsSpan(index + search.Length));
    }

    private static Task WritePreservingLineEndingsAsync(
        string path,
        string rawOriginal,
        string newContent,
        CancellationToken cancellationToken)
    {
        var content = DominatesCrlf(rawOriginal) ? newContent.Replace("\n", "\r\n") : newContent;
        return File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static bool DominatesCrlf(string content)
    {
        var crlfCount = 0;
        var lfCount = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n')
                continue;
            if (index > 0 && content[index - 1] == '\r')
                crlfCount++;
            else
                lfCount++;
        }
        return crlfCount > lfCount;
    }
}
