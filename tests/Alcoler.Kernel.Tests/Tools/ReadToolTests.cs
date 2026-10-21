using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;
using Xunit;
using SkiaSharp;

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
        Assert.Equal("0\tone\n1\ttwo", result.SingleText());
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
    public async Task InvalidImage_ReturnsDecodeError()
    {
        using var workspace = new TempWorkspace();
        // 4 MB of raw bytes encode past the 5 MB base64 budget.
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "big.png"), new byte[4 * 1024 * 1024 + 1]);
        var tool = new ReadTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "big.png"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Unable to decode image data", result.SingleText());
    }

    [Theory]
    [InlineData(3_000, 1_200, false)]
    [InlineData(1_000, 1_000, true)]
    public async Task OversizedImage_IsResizedAndRecompressedWithinModelBudget(int width, int height, bool noise)
    {
        using var workspace = new TempWorkspace();
        using var bitmap = new SKBitmap(width, height);
        if (noise)
        {
            var random = new Random(42);
            var pixels = new SKColor[width * height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            bitmap.Pixels = pixels;
        }
        else
        {
            bitmap.Erase(SKColors.Blue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "large.png"), png.ToArray());

        var result = await new ReadTool().ExecuteAsync(workspace.ContextFor("""{"file_path":"large.png"}"""), CancellationToken.None);

        Assert.False(result.IsError);
        var media = Assert.IsType<Alcoler.Contracts.Conversation.ToolResultMedia>(Assert.Single(result.Content));
        Assert.Equal("image/jpeg", media.MimeType);
        var output = await File.ReadAllBytesAsync(Path.Combine(workspace.Root, ".alcoler", "artifacts", "sess_test", media.FileName!));
        using var decoded = SKBitmap.Decode(output);
        Assert.InRange(Math.Max(decoded.Width, decoded.Height), 1, 2_000);
        Assert.True(Math.Ceiling(Convert.ToBase64String(output).Length * 0.125) <= 25_000);
        if (noise)
            Assert.True(output.Length < png.Size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRead_ContentUnchangedAfterTouch_AllowsEditOrWrite(bool write)
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "touch.txt");
        await File.WriteAllTextAsync(path, "original");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(workspace.ContextFor("""{"file_path":"touch.txt"}""") with { FileReads = reads }, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        Alcoler.Contracts.Tools.ITool tool = write ? new WriteTool() : new EditTool();
        var input = write ? """{"file_path":"touch.txt","content":"changed"}"""
            : """{"file_path":"touch.txt","old_string":"original","new_string":"changed"}""";
        var result = await tool.ExecuteAsync(workspace.ContextFor(input) with { FileReads = reads }, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("changed", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadViewCache_IsRangeSpecific_AndExplicitRangeAuthorizesEdit()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "range.txt");
        await File.WriteAllTextAsync(path, "first\nsecond\nthird");
        var reads = new FileReadState();
        var tool = new ReadTool();
        var whole = workspace.ContextFor("""{"file_path":"range.txt"}""") with { FileReads = reads };
        await tool.ExecuteAsync(whole, CancellationToken.None);
        var range = workspace.ContextFor("""{"file_path":"range.txt","offset":2,"limit":1}""") with { FileReads = reads };
        Assert.Equal("2\tsecond", (await tool.ExecuteAsync(range, CancellationToken.None)).SingleText());
        Assert.Contains("Wasted call", (await tool.ExecuteAsync(range, CancellationToken.None)).SingleText());
        var edited = await new EditTool().ExecuteAsync(workspace.ContextFor(
            """{"file_path":"range.txt","old_string":"second","new_string":"changed"}""") with { FileReads = reads }, CancellationToken.None);
        Assert.False(edited.IsError);
        Assert.Equal("first\nchanged\nthird", await File.ReadAllTextAsync(path));
    }
}
