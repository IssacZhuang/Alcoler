using Xunit;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class GlobToolTests
{
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
    public async Task Results_AreSorted_AndCappedAt500()
    {
        using var workspace = new TempWorkspace();
        var names = Enumerable.Range(0, 505).Select(i => $"f{i:D4}.tmp").ToArray();
        CreateFiles(workspace.Root, names);
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "*.tmp"}"""),
            CancellationToken.None);

        var lines = result.SingleText().Split('\n');
        Assert.Equal(501, lines.Length);
        Assert.Contains("capped at 500", lines[^1]);
        var paths = lines[..500];
        Assert.Equal(paths.OrderBy(static p => p, StringComparer.Ordinal), paths);
    }

    [Fact]
    public async Task NoMatches_ReportsEmptyResult()
    {
        using var workspace = new TempWorkspace();
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"pattern": "**/*.xyz"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("no files matched", result.SingleText());
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
