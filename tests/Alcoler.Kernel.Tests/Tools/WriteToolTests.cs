using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Storage;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class WriteToolTests
{
    [Fact]
    public async Task CreatesNewFile_CreatesDirectories_ReportsCreatedMessage()
    {
        using var workspace = new TempWorkspace();
        var tool = new WriteTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "sub/dir/new.txt", "content": "line one\nline two"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var written = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "sub", "dir", "new.txt"));
        Assert.Equal("line one\nline two", written);
        Assert.Equal(
            "File created successfully at: sub/dir/new.txt (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
        Assert.Equal(new FileChangeStats(2, 0), result.FileChange);
    }

    [Fact]
    public async Task OverwriteWithoutPriorRead_RejectedWithVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "original");
        var tool = new WriteTool();

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
        var reader = new ReadTool();
        var tool = new WriteTool();

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
        Assert.Equal(new FileChangeStats(1, 1), result.FileChange);
    }

    [Fact]
    public async Task Overwrite_ReportsLineChangesAgainstPreviousContent()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree");
        var reads = new FileReadState();
        var reader = new ReadTool();
        var tool = new WriteTool();

        await reader.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "one\nTWO\nthree\nfour"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new FileChangeStats(2, 1), result.FileChange);
    }

    [Fact]
    public async Task OverwriteAfterExternalModification_RejectedAsStale()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "original");
        var reads = new FileReadState();
        var reader = new ReadTool();
        var tool = new WriteTool();

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
        var tool = new WriteTool();

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
    public async Task OverwriteAfterExplicitRangeRead_Succeeds()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "a.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree");
        var reads = new FileReadState();
        var reader = new ReadTool();
        var tool = new WriteTool();

        await reader.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "limit": 2}""") with { FileReads = reads },
            CancellationToken.None);

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "content": "replacement"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OverwriteAfterTokenTruncatedRead_IsRejected()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', 100_000));
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(workspace.ContextFor("""{"file_path":"large.txt"}""") with { FileReads = reads }, CancellationToken.None);
        var result = await new WriteTool().ExecuteAsync(workspace.ContextFor(
            """{"file_path":"large.txt","content":"replacement"}""") with { FileReads = reads }, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("File has not been read yet. Read it first before writing to it.", result.SingleText());
    }

    [Fact]
    public async Task WritesFileOutsideWorkspace()
    {
        using var workspace = new TempWorkspace();
        using var external = new TempDirectory();
        var path = Path.Combine(external.Root, "outside.txt");
        var tool = new WriteTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(JsonSerializer.Serialize(new { file_path = path, content = "outside" })),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("outside", await File.ReadAllTextAsync(path));
    }
}
