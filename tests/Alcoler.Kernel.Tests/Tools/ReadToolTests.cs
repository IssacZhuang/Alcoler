using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class ReadToolTests
{
    [Fact]
    public async Task ReadsFile_WithCatStyleNumberedLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "alpha\nbeta\ngamma");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("1\talpha", text);
        Assert.Contains("2\tbeta", text);
        Assert.Contains("3\tgamma", text);
    }

    [Fact]
    public async Task ReadsFile_UsesUnpaddedLineNumbers()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "alpha\nbeta\ngamma");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Equal("1\talpha\n2\tbeta\n3\tgamma", text);
        Assert.DoesNotContain("     1\talpha", text);
    }

    [Fact]
    public async Task OffsetZero_IsAccepted_AndReadsFromFirstLine()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "one\ntwo");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "offset": 0}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("1\tone\n2\ttwo", result.SingleText());
    }

    [Fact]
    public async Task OffsetAndLimit_PageThroughLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "one\ntwo\nthree\nfour");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "offset": 2, "limit": 1}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("2\ttwo", text);
        Assert.DoesNotContain("one", text);
        Assert.DoesNotContain("three", text);
    }

    [Fact]
    public async Task DefaultLimit_CapsAtTwoThousandLines()
    {
        using var workspace = new TempWorkspace();
        var content = string.Join("\n", Enumerable.Range(1, 2_500).Select(i => $"line {i}"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "big-count.txt"), content);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "big-count.txt"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("2000\tline 2000", text);
        Assert.DoesNotContain("line 2001", text);
    }

    [Fact]
    public async Task MissingFile_ReturnsCwdNoteWithLevenshteinSuggestion()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "notes.txt"), "content");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "note.txt"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            $"File does not exist. Note: your current working directory is {workspace.Root}. Did you mean notes.txt?",
            result.SingleText());
    }

    [Fact]
    public async Task MissingFile_SameStemSibling_PreferedOverLevenshtein()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "notes.txt"), "content");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "notes.md"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.EndsWith(" Did you mean notes.txt?", result.SingleText());
    }

    [Fact]
    public async Task BinaryExtension_ReturnsVerbatimError()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "app.zip"), "pk");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "app.zip"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "This tool cannot read binary files. The file appears to be a binary .zip file. Please use appropriate tools for binary file analysis.",
            result.SingleText());
    }

    [Fact]
    public async Task EmptyFile_ReturnsEmptyContentsReminder()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "empty.txt"), "");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "empty.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>",
            result.SingleText());
    }

    [Fact]
    public async Task OffsetBeyondEnd_ReturnsShorterThanOffsetReminder()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "a\nb\nc");
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "offset": 7}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "<system-reminder>Warning: the file exists but is shorter than the provided offset (7). The file has 3 lines.</system-reminder>",
            result.SingleText());
    }

    [Fact]
    public async Task OversizedFile_WithoutLimit_ReturnsTooLargeError()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "huge.txt"), new string('x', 300_000));
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "huge.txt"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "File content (293KB) exceeds maximum allowed size (256KB). Use offset and limit parameters to read specific portions of the file, or search for specific content instead of reading the whole file.",
            result.SingleText());
    }

    [Fact]
    public async Task OversizedFile_WithExplicitLimit_BypassesSizeCap()
    {
        using var workspace = new TempWorkspace();
        var content = string.Join("\n", Enumerable.Repeat("aaaaa", 60_000));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "huge.txt"), content);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "huge.txt", "limit": 10}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("10\taaaaa", result.SingleText());
    }

    [Fact]
    public async Task TokenCap_WholeFileRead_ReturnsPartialViewWithContinueHint()
    {
        using var workspace = new TempWorkspace();
        var content = string.Join("\n", Enumerable.Repeat(new string('x', 500), 250));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "tokens.txt"), content);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "tokens.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith(
            "<system-reminder>The file is too large to display in full (41750 estimated tokens, limit 25000). "
            + "Showing a partial view of lines 1-127 of 250. "
            + "Use Read with offset 128 and limit 2000 to continue, or use a search tool to find a specific section.</system-reminder>\n\n",
            text);
        Assert.Contains("127\t", text);
        Assert.DoesNotContain("128\t", text);
    }

    [Fact]
    public async Task TokenCap_ContinuationFromHint_ReturnsRemainingLines()
    {
        using var workspace = new TempWorkspace();
        var content = string.Join("\n", Enumerable.Repeat(new string('x', 500), 250));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "tokens.txt"), content);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "tokens.txt", "offset": 128}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.DoesNotContain("system-reminder", text);
        Assert.Contains("128\t", text);
        Assert.Contains("250\t", text);
    }

    [Fact]
    public async Task TokenCap_ExplicitRangeOverBudget_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one-line.txt"), new string('y', 100_000));
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "one-line.txt", "offset": 1, "limit": 1}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "File content (33334 tokens) exceeds maximum allowed tokens (25000). Use offset and limit parameters to read specific portions of the file, or search for specific content instead of reading the whole file.",
            result.SingleText());
    }

    [Fact]
    public async Task RepeatedReadOfUnchangedFile_ReturnsWastedCallStub()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta");
        var reads = new FileReadState();
        var tool = new ReadTool();

        var first = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(
            "Wasted call \u2014 file unchanged since your last Read. Refer to that earlier tool_result instead.",
            second.SingleText());
    }

    [Fact]
    public async Task ExternallyModifiedFile_RereadReturnsFreshContent()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "alpha");
        var reads = new FileReadState();
        var tool = new ReadTool();

        await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma");

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("gamma", result.SingleText());
    }

    [Fact]
    public async Task PartialView_DoesNotStubSubsequentRead()
    {
        using var workspace = new TempWorkspace();
        var content = string.Join("\n", Enumerable.Repeat(new string('x', 500), 250));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "tokens.txt"), content);
        var reads = new FileReadState();
        var tool = new ReadTool();

        var first = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "tokens.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "tokens.txt"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.Contains("partial view", first.SingleText());
        Assert.Contains("partial view", second.SingleText());
    }

    [Fact]
    public async Task PathEscapingWorkspace_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "../outside.txt"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("escapes", result.SingleText());
    }

    [Fact]
    public async Task ImageFile_ReturnsMediaBlockSavedAsArtifact()
    {
        using var workspace = new TempWorkspace();
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "shot.png"), png);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "shot.png"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var media = Assert.IsType<Alcoler.Contracts.Conversation.ToolResultMedia>(Assert.Single(result.Content));
        Assert.Equal("image/png", media.MimeType);
        Assert.Equal("shot.png", media.FileName);
        Assert.StartsWith("alcoler-artifact://", media.ArtifactUri, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            workspace.Root, ".alcoler", "artifacts", "sess_test", "shot.png")));
    }

    [Fact]
    public async Task ImageOverBase64Budget_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        // 4 MB of raw bytes encode past the 5 MB base64 budget.
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "big.png"), new byte[4 * 1024 * 1024 + 1]);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "big.png"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("too large", result.SingleText());
    }
}
