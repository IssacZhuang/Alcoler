using System.Text.Json;
using Xunit;
using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tests.Storage;

namespace Alcoler.Kernel.Tests.Tools;

public class GrepToolTests
{
    [Fact]
    public async Task OversizedOutput_UsesArtifactEnvelopeAndDecimalBytes()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "large.txt"), string.Join("\n", Enumerable.Repeat("hit " + new string('x', 200), 200)));
        var result = await new GrepTool().ExecuteAsync(workspace.ContextFor(
            """{"pattern":"hit","output_mode":"content","head_limit":0}"""), CancellationToken.None);
        Assert.StartsWith("<persisted-output>", result.SingleText());
        Assert.Contains(" KB). Full output saved to: ", result.SingleText());
        Assert.Contains("Preview (first 2 KB):", result.SingleText());
        Assert.DoesNotContain("truncated by resultBudget", result.SingleText());
        Assert.Single(Directory.GetFiles(Path.Combine(workspace.Root, ".alcoler", "artifacts", "sess_test")));
    }

    [Fact]
    public void Contract_PathDescription_MatchesReferenceWording()
    {
        var path = new GrepTool().Contract.InputSchema.GetProperty("properties").GetProperty("path");

        Assert.Equal(
            "File or directory to search in (rg PATH). Defaults to current working directory.",
            path.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ContentMode_FormatsPathLineNumberText()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world\nnothing here");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "two.txt"), "nope\nhello again");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var lines = result.SingleText().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("one.txt:1:hello world", lines);
        Assert.Contains("two.txt:2:hello again", lines);
    }

    [Fact]
    public async Task DefaultMode_IsFilesWithMatches_SortedByMtime()
    {
        using var workspace = new TempWorkspace();
        var older = Path.Combine(workspace.Root, "older.txt");
        var newer = Path.Combine(workspace.Root, "newer.txt");
        await File.WriteAllTextAsync(older, "hello world");
        await File.WriteAllTextAsync(newer, "hello again");
        File.SetLastWriteTimeUtc(older, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 2 files\nnewer.txt\nolder.txt", result.SingleText());
    }

    [Fact]
    public async Task CountMode_ReportsPerFileCountsAndTotalSummary()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "hit\nhit\nmiss");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.txt"), "hit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "count"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("a.txt:2\nb.txt:1\n\nFound 3 total occurrences across 2 files.", result.SingleText());
    }

    [Fact]
    public async Task HeadLimit_TruncatesContentAndNotesPagination()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "many.txt"), "hit\nhit\nhit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "content", "head_limit": 2}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "many.txt:1:hit\nmany.txt:2:hit\n\n[Showing results with pagination = limit: 2]",
            result.SingleText());
    }

    [Fact]
    public async Task DefaultHeadLimit_IsAppliedWhenResultsExceed250()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "many.txt"),
            string.Join("\n", Enumerable.Repeat("hit", 260)));
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "content"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.EndsWith("\n\n[Showing results with pagination = limit: 250]", text);
        Assert.Equal(250, text.Split('\n').Count(static line => line.StartsWith("many.txt:")));
    }

    [Fact]
    public async Task HeadLimitZero_ReturnsEverythingWithoutPaginationNote()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "many.txt"), "hit\nhit\nhit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "content", "head_limit": 0}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(3, result.SingleText().Split('\n').Length);
        Assert.DoesNotContain("[Showing", result.SingleText());
    }

    [Fact]
    public async Task FilesMode_HeadLimitTruncatesPathsAndNotesPagination()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "newer.txt"), "hit");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "older.txt"), "hit");
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Root, "newer.txt"), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Root, "older.txt"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "head_limit": 1}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 1 file limit: 1\nnewer.txt", result.SingleText());
    }

    [Fact]
    public async Task CountMode_HeadLimitKeepsTotalsAndNotesPagination()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "hit\nhit\nhit");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.txt"), "hit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "count", "head_limit": 1}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("a.txt:3\n\nFound 4 total occurrences across 2 files. with pagination = limit: 1", result.SingleText());
    }

    [Fact]
    public async Task Offset_SkipsEntriesAndNotesOffset()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "many.txt"), "hit\nhit\nhit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "content", "offset": 1}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "many.txt:2:hit\nmany.txt:3:hit\n\n[Showing results with pagination = offset: 1]",
            result.SingleText());
    }

    [Fact]
    public async Task ContextFlag_IncludesSurroundingLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "ctx.txt"), "a\nb\nc\nd\ne");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "c", "output_mode": "content", "-C": 1}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ctx.txt:2:b\nctx.txt:3:c\nctx.txt:4:d", result.SingleText());
    }

    [Fact]
    public async Task BeforeAndAfterContext_CanDiffer()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "ctx.txt"), "a\nb\nc\nd\ne");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "c", "output_mode": "content", "-B": 2, "-A": 1}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ctx.txt:1:a\nctx.txt:2:b\nctx.txt:3:c\nctx.txt:4:d", result.SingleText());
    }

    [Fact]
    public async Task IgnoreCase_MatchesDifferentCase()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "shout.txt"), "HELLO world");
        var tool = new GrepTool();

        var insensitive = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content", "-i": true}"""),
            CancellationToken.None);
        Assert.Equal("shout.txt:1:HELLO world", insensitive.SingleText());

        var sensitive = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content"}"""),
            CancellationToken.None);
        Assert.Equal("No matches found", sensitive.SingleText());
    }

    [Fact]
    public async Task TypeFilter_MapsToExtensions()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.cs"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.py"), "target");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target", "output_mode": "content", "type": "cs"}"""),
            CancellationToken.None);

        Assert.Equal("a.cs:1:target", result.SingleText());
    }

    [Fact]
    public async Task UnknownType_FallsBackToExtensionMatch()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "data.xyz"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "keep.txt"), "target");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target", "output_mode": "content", "type": "xyz"}"""),
            CancellationToken.None);

        Assert.Equal("data.xyz:1:target", result.SingleText());
    }

    [Fact]
    public async Task SearchesDirectoryOutsideWorkspace()
    {
        using var workspace = new TempWorkspace();
        using var external = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(external.Root, "outside.txt"), "needle");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(JsonSerializer.Serialize(new
            {
                pattern = "needle",
                path = external.Root,
                output_mode = "files_with_matches",
            })),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("outside.txt", result.SingleText());
    }

    [Fact]
    public async Task NoMatches_ContentMode_AndFilesMode()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "nothing here");
        var tool = new GrepTool();

        var content = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content"}"""),
            CancellationToken.None);
        Assert.Equal("No matches found", content.SingleText());

        var files = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello"}"""),
            CancellationToken.None);
        Assert.Equal("No files found", files.SingleText());
    }

    [Fact]
    public async Task LineNumbers_CanBeDisabled()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content", "-n": false}"""),
            CancellationToken.None);

        Assert.Equal("one.txt:hello world", result.SingleText());
    }

    [Fact]
    public async Task OnlyMatching_PrintsEachMatchOnItsOwnLine()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "o.txt"), "foo bar baz qux");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "b\\w+", "output_mode": "content", "-o": true}"""),
            CancellationToken.None);

        Assert.Equal("o.txt:1:bar\no.txt:1:baz", result.SingleText());
    }

    [Fact]
    public async Task Multiline_MatchesAcrossLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "m.txt"), "start\nmiddle\nend");
        var tool = new GrepTool();

        var multiline = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "start.*end", "output_mode": "content", "multiline": true}"""),
            CancellationToken.None);
        Assert.Equal("m.txt:1:start", multiline.SingleText());

        var single = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "start.*end", "output_mode": "content"}"""),
            CancellationToken.None);
        Assert.Equal("No matches found", single.SingleText());
    }

    [Fact]
    public async Task GlobFilter_RestrictsFileNames_IncludingBraces()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "keep.txt"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "skip.log"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.ts"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.tsx"), "target");
        var tool = new GrepTool();

        var simple = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target", "output_mode": "content", "glob": "*.txt"}"""),
            CancellationToken.None);
        Assert.Equal("keep.txt:1:target", simple.SingleText());

        var braces = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target", "output_mode": "content", "glob": "*.{ts,tsx}"}"""),
            CancellationToken.None);
        Assert.Equal("a.ts:1:target\nb.tsx:1:target", braces.SingleText());
    }

    [Fact]
    public async Task Path_CanTargetASingleFile()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "two.txt"), "hello again");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello", "output_mode": "content", "path": "one.txt"}"""),
            CancellationToken.None);

        Assert.Equal("one.txt:1:hello world", result.SingleText());
    }

    [Fact]
    public async Task BinaryFiles_AreSkipped()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "text.txt"), "hello there");
        var binary = new byte[] { 0x68, 0x00, 0x65, 0x6C, 0x6C, 0x6F };
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "blob.bin"), binary);
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 1 file\ntext.txt", result.SingleText());
    }

    [Fact]
    public async Task VersionControlDirectories_AreSkipped()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".git", "config.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "hello.txt"), "hello");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 1 file\nhello.txt", result.SingleText());
    }

    [Fact]
    public async Task InvalidRegex_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "("}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Invalid grep regular expression: (", result.SingleText());
    }

    [Fact]
    public async Task WhitespaceOnlyPattern_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "   "}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Grep pattern must not be empty", result.SingleText());
    }
}
