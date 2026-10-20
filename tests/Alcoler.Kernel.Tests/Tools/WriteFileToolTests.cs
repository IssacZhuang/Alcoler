using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class WriteFileToolTests
{
    [Fact]
    public async Task CreatesNewFile_CreatesDirectories_ReportsCreatedMessage()
    {
        using var workspace = new TempWorkspace();
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "sub/dir/new.txt", "content": "line one\nline two"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var written = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "sub", "dir", "new.txt"));
        Assert.Equal("line one\nline two", written);
        Assert.Equal(
            "File created successfully at: sub/dir/new.txt (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
    }

    [Fact]
    public async Task OverwriteWithoutPriorRead_RejectedWithVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "original");
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "replacement"}""") with { FileReads = new FileReadState() },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("File has not been read yet. Read it first before writing to it.", result.SingleText());
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OverwriteAfterRead_ReportsUpdatedMessage()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "original");
        var reads = new FileReadState();
        var reader = new ReadFileTool();
        var tool = new WriteFileTool();

        await reader.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "replacement"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        Assert.Equal(
            "The file a.txt has been updated successfully. (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
    }

    [Fact]
    public async Task OverwriteAfterExternalModification_RejectedAsStale()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "original");
        var reads = new FileReadState();
        var reader = new ReadFileTool();
        var tool = new WriteFileTool();

        await reader.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        await File.WriteAllTextAsync(path, "externally rewritten content");

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "replacement"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.",
            result.SingleText());
    }

    [Fact]
    public async Task WriteAfterWrite_SucceedsBecauseStateIsCurrent()
    {
        using var workspace = new TempWorkspace();
        var reads = new FileReadState();
        var tool = new WriteFileTool();

        var first = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "one"}""") with { FileReads = reads },
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "two"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "a.txt")));
        Assert.Contains("has been updated successfully", second.SingleText());
    }

    [Fact]
    public async Task OverwriteAfterRangeRead_RejectedAsIncompleteView()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree");
        var reads = new FileReadState();
        var reader = new ReadFileTool();
        var tool = new WriteFileTool();

        await reader.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "limit": 2}""") with { FileReads = reads },
            CancellationToken.None);

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "replacement"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("File has not been read yet. Read it first before writing to it.", result.SingleText());
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
