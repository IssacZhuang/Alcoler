using Xunit;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class WriteFileToolTests
{
    [Fact]
    public async Task WritesFile_CreatesDirectories_ReportsPathAndLineCount()
    {
        using var workspace = new TempWorkspace();
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "sub/dir/new.txt", "content": "line one\nline two"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var written = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "sub", "dir", "new.txt"));
        Assert.Equal("line one\nline two", written);
        var text = result.SingleText();
        Assert.Contains(Path.Combine(workspace.Root, "sub", "dir", "new.txt"), text);
        Assert.Contains("2 lines", text);
    }

    [Fact]
    public async Task PathEscapingWorkspace_Rejected_NoFileWrittenOutside()
    {
        using var workspace = new TempWorkspace();
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "../evil.txt", "content": "nope"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("escapes", result.SingleText());
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.Root, "..", "evil.txt"))));
    }
}
