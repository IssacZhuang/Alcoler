using Xunit;
using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class ReadFileToolTests
{
    [Fact]
    public async Task ReadsFile_WithCatStyleNumberedLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "alpha\nbeta\ngamma");
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("     1\talpha", text);
        Assert.Contains("     2\tbeta", text);
        Assert.Contains("     3\tgamma", text);
    }

    [Fact]
    public async Task OffsetAndLimit_PageThroughLines()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), "one\ntwo\nthree\nfour");
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "offset": 2, "limit": 1}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("     2\ttwo", text);
        Assert.DoesNotContain("one", text);
        Assert.DoesNotContain("three", text);
    }

    [Fact]
    public async Task MissingFile_ReturnsErrorWithConstraintMessage()
    {
        using var workspace = new TempWorkspace();
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "missing.txt"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("does not exist", result.SingleText());
    }

    [Fact]
    public async Task OversizedContent_TruncatedWithHintToUseOffsetAndLimit()
    {
        using var workspace = new TempWorkspace();
        var bigLine = new string('x', 100);
        var content = string.Join("\n", Enumerable.Range(0, 1000).Select(_ => bigLine));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "big.txt"), content);
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "big.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("truncated", text);
        Assert.Contains("offset", text);
        Assert.True(text.Length <= ToolResultLimits.ReadToolMaxChars + 200);
    }

    [Fact]
    public async Task PathEscapingWorkspace_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "../outside.txt"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("escapes", result.SingleText());
    }
}
