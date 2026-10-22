using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Storage;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class EditToolTests
{
    [Fact]
    public async Task UniqueMatch_AfterRead_ReplacesAndReportsVerbatimMessage()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta\nepsilon");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "delta", "new_string": "DELTA"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nbeta\ngamma\nDELTA\nepsilon", await File.ReadAllTextAsync(path));
        Assert.Equal(
            "The file code.txt has been updated successfully. (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
    }

    [Fact]
    public async Task IdenticalOldAndNew_ReturnsVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "a.txt", "old_string": "same", "new_string": "same"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("No changes to make: old_string and new_string are exactly the same.", result.SingleText());
    }

    [Fact]
    public async Task AmbiguousMatch_ReturnsVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        var original = "one beta\nother beta";
        await File.WriteAllTextAsync(path, original);
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "x"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "Found 2 matches of the string to replace, but replace_all is false. "
            + "To replace all occurrences, set replace_all to true. "
            + "To replace only one occurrence, please provide more context to uniquely identify the instance.\n"
            + "String: beta",
            result.SingleText());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReplaceAll_ReplacesEveryOccurrence_ReportsAllReplacedMessage()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "one beta\nother beta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "x", "replace_all": true}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("one x\nother x", await File.ReadAllTextAsync(path));
        Assert.Equal(
            "The file code.txt has been updated. All occurrences were successfully replaced. (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("true")]
    public async Task ReplaceAll_AcceptsSemanticBooleanStrings(string replaceAll)
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "beta beta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var json = $$"""{"file_path": "code.txt", "old_string": "beta", "new_string": "x", "replace_all": "{{replaceAll}}"}""";
        var result = await tool.ExecuteAsync(
            workspace.ContextFor(json) with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("x x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NoMatch_ReturnsVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "content");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "absent", "new_string": "x"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("String to replace not found in file.\nString: absent", result.SingleText());
        Assert.Equal("content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditWithoutRead_RejectedWithVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "content");
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "content", "new_string": "x"}""") with { FileReads = new FileReadState() },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("File has not been read yet. Read it first before writing to it.", result.SingleText());
        Assert.Equal("content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditAfterExternalModification_RejectedAsStale()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "original");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        await File.WriteAllTextAsync(path, "externally rewritten content");
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "externally rewritten content", "new_string": "x"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.",
            result.SingleText());
    }

    [Fact]
    public async Task MissingFile_ReturnsCwdNoteWithSuggestion()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "notes.txt"), "content");
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "note.txt", "old_string": "a", "new_string": "b"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            $"File does not exist. Note: your current working directory is {workspace.Root}. Did you mean notes.txt?",
            result.SingleText());
    }

    [Fact]
    public async Task NotebookFile_ReturnsVerbatimError_AndLeavesFileUnchanged()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "notebook.ipynb");
        await File.WriteAllTextAsync(path, "{\"cells\": []}");
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "notebook.ipynb", "old_string": "cells", "new_string": "x"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("File is a Jupyter Notebook. Use the NotebookEdit to edit this file.", result.SingleText());
        Assert.Equal("{\"cells\": []}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OversizedFile_OverOneGb_ReturnsVerbatimError()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "huge.txt");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            stream.SetLength(1_073_741_825L);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "huge.txt", "old_string": "a", "new_string": "b"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("File is too large to edit (1GB). Maximum editable file size is 1GB.", result.SingleText());
    }

    [Fact]
    public async Task EmptyOldString_ExistingNonEmptyFile_Rejected()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "content");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "", "new_string": "fresh"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Cannot create new file - file already exists.", result.SingleText());
        Assert.Equal("content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptyOldString_MissingFile_CreatesNewFile()
    {
        using var workspace = new TempWorkspace();
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "fresh.txt", "old_string": "", "new_string": "hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "fresh.txt")));
        Assert.Equal(
            "The file fresh.txt has been updated successfully. (file state is current in your context \u2014 no need to Read it back)",
            result.SingleText());
    }

    [Fact]
    public async Task QuoteNormalizedMatch_ReplacementKeepsCurlyQuotes()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "doc.txt");
        await File.WriteAllTextAsync(path, "say \u201Chello\u201D now");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "doc.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "doc.txt", "old_string": "say \"hello\" now", "new_string": "say \"goodbye\" now"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("say \u201Cgoodbye\u201D now", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LineNumberPrefix_StrippedBeforeMatching()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "4: delta", "new_string": "4: DELTA"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nbeta\ngamma\n4: DELTA", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CrlfFile_EditsPreserveCrlfLineEndings()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "crlf.txt");
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "crlf.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "crlf.txt", "old_string": "beta", "new_string": "BETA"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\r\nBETA\r\ngamma", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeletingALine_SwallowsTheTrailingNewline()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta\n", "new_string": ""}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\ngamma", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditAfterEdit_SucceedsBecauseStateIsCurrent()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var first = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "alpha", "new_string": "ALPHA"}""") with { FileReads = reads },
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "BETA"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal("ALPHA\nBETA", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SuccessfulEdit_ReportsAddedAndRemovedLineCounts()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "BETA"}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new FileChangeStats(1, 1), result.FileChange);
    }

    [Fact]
    public async Task DeletingLines_ReportsOnlyRemovedCount()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta\n", "new_string": ""}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new FileChangeStats(0, 1), result.FileChange);
    }

    [Fact]
    public async Task ReplaceAll_ReportsEveryChangedLine()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "code.txt");
        await File.WriteAllTextAsync(path, "one beta\nother beta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt"}""") with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "code.txt", "old_string": "beta", "new_string": "x", "replace_all": true}""") with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new FileChangeStats(2, 2), result.FileChange);
    }

    [Fact]
    public async Task CreatingFile_ReportsAddedLineCount()
    {
        using var workspace = new TempWorkspace();
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"file_path": "fresh.txt", "old_string": "", "new_string": "one\ntwo"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new FileChangeStats(2, 0), result.FileChange);
    }

    [Fact]
    public async Task EditsFileOutsideWorkspace_AfterRead()
    {
        using var workspace = new TempWorkspace();
        using var external = new TempDirectory();
        var path = Path.Combine(external.Root, "outside.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta");
        var reads = new FileReadState();
        await new ReadTool().ExecuteAsync(
            workspace.ContextFor(JsonSerializer.Serialize(new { file_path = path })) with { FileReads = reads },
            CancellationToken.None);
        var tool = new EditTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(JsonSerializer.Serialize(new
            {
                file_path = path,
                old_string = "beta",
                new_string = "BETA",
            })) with { FileReads = reads },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nBETA", await File.ReadAllTextAsync(path));
    }
}
