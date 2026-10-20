using Xunit;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class GlobToolTests
{
    [Fact]
    public void Contract_PathDescription_MatchesReferenceWording()
    {
        var path = new GlobTool().Contract.InputSchema.GetProperty("properties").GetProperty("path");

        Assert.Equal(
            "The directory to search in. If not specified, the current working directory will be used. IMPORTANT: Omit this field to use the default directory. DO NOT enter \"undefined\" or \"null\" - simply omit it for the default behavior. Must be a valid directory path if provided.",
            path.GetProperty("description").GetString());
    }

    [Fact]
    public async Task DoubleStarPattern_MatchesRecursively()
    {
        using var workspace = new TempWorkspace();
        CreateFiles(workspace.Root,
        [
            "root.cs",
            "sub/nested.cs",
            "sub/deep/leaf.cs",
            "notes/readme.txt",
        ]);
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "**/*.cs"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("root.cs", text);
        Assert.Contains("sub/nested.cs", text);
        Assert.Contains("sub/deep/leaf.cs", text);
        Assert.DoesNotContain("readme.txt", text);
    }

    [Fact]
    public async Task SingleStarPattern_MatchesOnlyRootLevel()
    {
        using var workspace = new TempWorkspace();
        CreateFiles(workspace.Root, ["a.cs", "sub/b.cs"]);
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "*.cs"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("a.cs", text);
        Assert.DoesNotContain("sub/", text);
    }

    [Fact]
    public async Task Results_AreSortedByModificationTime_NewestFirst()
    {
        using var workspace = new TempWorkspace();
        var older = Path.Combine(workspace.Root, "older.tmp");
        var newer = Path.Combine(workspace.Root, "newer.tmp");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(older, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "*.tmp"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("newer.tmp\nolder.tmp", result.SingleText());
    }

    [Fact]
    public async Task EqualMtimes_FallBackToPathOrder()
    {
        using var workspace = new TempWorkspace();
        var sameTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = Path.Combine(workspace.Root, "a.tmp");
        var second = Path.Combine(workspace.Root, "b.tmp");
        File.WriteAllText(first, "x");
        File.WriteAllText(second, "x");
        File.SetLastWriteTimeUtc(first, sameTime);
        File.SetLastWriteTimeUtc(second, sameTime);
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "*.tmp"}"""),
            CancellationToken.None);

        Assert.Equal("a.tmp\nb.tmp", result.SingleText());
    }

    [Fact]
    public async Task Results_AreCappedAt100_WithTruncationNotice()
    {
        using var workspace = new TempWorkspace();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 105; i++)
        {
            var path = Path.Combine(workspace.Root, $"f{i:D4}.tmp");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(-i));
        }
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "*.tmp"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var lines = result.SingleText().Split('\n');
        Assert.Equal(101, lines.Length);
        Assert.Equal("f0000.tmp", lines[0]);
        Assert.Equal("f0099.tmp", lines[99]);
        Assert.Equal("(Results are truncated. Consider using a more specific path or pattern.)", lines[^1]);
    }

    [Fact]
    public async Task NoMatches_ReportsNoFilesFound()
    {
        using var workspace = new TempWorkspace();
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "**/*.xyz"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("No files found", result.SingleText());
    }

    [Fact]
    public async Task VersionControlDirectories_AreSkipped()
    {
        using var workspace = new TempWorkspace();
        CreateFiles(workspace.Root, [".git/inner.tmp", "keep.tmp"]);
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "**/*.tmp"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("keep.tmp", result.SingleText());
    }

    private static void CreateFiles(string root, IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "x");
        }
    }
}
