using Xunit;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class EditFileToolTests
{
    [Fact]
    public async Task UniqueMatch_Replaced_WithNumberedPreview()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta\nepsilon\nzeta\neta\ntheta");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "delta", "new_string": "DELTA"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nbeta\ngamma\nDELTA\nepsilon\nzeta\neta\ntheta", await File.ReadAllTextAsync(path));
        var text = result.SingleText();
        Assert.Contains("     2\tbeta", text);
        Assert.Contains("     4\tDELTA", text);
        Assert.Contains("     7\teta", text);
    }

    [Fact]
    public async Task AmbiguousMatch_ReturnsError_SuggestsMoreContext()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        var original = "one beta\nother beta";
        await File.WriteAllTextAsync(path, original);
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "x"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        var text = result.SingleText();
        Assert.Contains("2", text);
        Assert.Contains("replace_all", text);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReplaceAll_ReplacesEveryOccurrence()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "one beta\nother beta");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "x", "replace_all": true}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("one x\nother x", await File.ReadAllTextAsync(path));
        Assert.Contains("2 occurrence(s)", result.SingleText());
    }

    [Fact]
    public async Task NoMatch_ReturnsError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "content");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "absent", "new_string": "x"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not found", result.SingleText());
        Assert.Equal("content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task IdenticalOldAndNew_RejectedByValidation()
    {
        var tool = new EditFileTool();

        var issue = await tool.ValidateInputAsync(
            System.Text.Json.JsonDocument.Parse("""{"file_path": "a.txt", "old_string": "same", "new_string": "same"}""").RootElement,
            CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Contains("differ", issue.Message);
    }
}
