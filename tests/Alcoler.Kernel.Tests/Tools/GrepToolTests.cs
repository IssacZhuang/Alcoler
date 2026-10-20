using Xunit;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class GrepToolTests
{
    [Fact]
    public async Task MatchesAcrossFiles_AsFileLineText()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world\nnothing here");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "two.txt"), "nope\nhello again");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var lines = result.SingleText().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("one.txt:1:hello world", lines);
        Assert.Contains("two.txt:2:hello again", lines);
    }

    [Fact]
    public async Task RegexMode_MatchesPatterns_LiteralModeDoesNot()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "r.txt"), "helllo");
        var tool = new GrepTool();

        var regexResult = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hel+o", "regex": true}"""),
            CancellationToken.None);
        Assert.Contains("r.txt:1:helllo", regexResult.SingleText());

        var literalResult = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hel+o"}"""),
            CancellationToken.None);
        Assert.Contains("no matches", literalResult.SingleText());
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

        var lines = result.SingleText().Split('\n').Where(static line => line.Contains(':')).ToList();
        var match = Assert.Single(lines);
        Assert.StartsWith("text.txt:1:", match);
    }

    [Fact]
    public async Task MaxResults_CapsOutputWithNote()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "many.txt"), "hit\nhit\nhit");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "max_results": 2}"""),
            CancellationToken.None);

        var lines = result.SingleText().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(2, lines.Count(static line => line.StartsWith("many.txt:")));
        Assert.Contains("capped at 2", lines[^1]);
    }

    [Fact]
    public async Task IncludeFilter_RestrictsFileNames()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "keep.txt"), "target");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "skip.log"), "target");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target", "include": "*.txt"}"""),
            CancellationToken.None);

        var lines = result.SingleText().Split('\n').Where(static line => line.Contains(':')).ToList();
        var match = Assert.Single(lines);
        Assert.StartsWith("keep.txt:1:", match);
    }

    [Fact]
    public async Task InvalidRegex_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "(", "regex": true}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Invalid regular expression", result.SingleText());
    }
}
