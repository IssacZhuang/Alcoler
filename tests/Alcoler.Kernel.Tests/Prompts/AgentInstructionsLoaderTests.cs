using System.Text;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Tests.Storage;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class AgentInstructionsLoaderTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _userHome = new();

    public void Dispose()
    {
        _root.Dispose();
        _userHome.Dispose();
    }

    private string UserFile
    {
        get
        {
            var dir = Path.Combine(_userHome.Root, ".alcoler");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "AGENTS.md");
        }
    }

    [Fact]
    public void Load_ReturnsUserScopeFirstThenWorkspace()
    {
        File.WriteAllText(UserFile, "user body");
        var workspace = Path.Combine(_root.Root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "ws body");

        var sources = AgentInstructionsLoader.Load(Path.Combine(workspace, "sub"), userHome: _userHome.Root);

        Assert.Equal(
            [(AgentInstructionScope.User, "user body"), (AgentInstructionScope.Workspace, "ws body")],
            sources.Select(s => (s.Scope, s.Content)).ToArray());
    }

    [Fact]
    public void Load_WalksUpToGitRootInclusive()
    {
        var gitRoot = Path.Combine(_root.Root, "repo");
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(gitRoot, "a", "b"));
        File.WriteAllText(Path.Combine(gitRoot, "AGENTS.md"), "repo body");

        var sources = AgentInstructionsLoader.Load(Path.Combine(gitRoot, "a", "b"), userHome: _userHome.Root);

        Assert.Equal("repo body", Assert.Single(sources).Content);
    }

    [Fact]
    public void Load_DoesNotLookAboveGitRoot()
    {
        var aboveGitRoot = Path.Combine(_root.Root, "outer", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(aboveGitRoot)!);
        File.WriteAllText(aboveGitRoot, "outer body");
        var gitRoot = Path.Combine(_root.Root, "outer", "repo");
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));

        Assert.Empty(AgentInstructionsLoader.Load(gitRoot, userHome: _userHome.Root));
    }

    [Fact]
    public void Load_NearestFileOnWalkUpWins()
    {
        var gitRoot = Path.Combine(_root.Root, "repo");
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(gitRoot, "a"));
        File.WriteAllText(Path.Combine(gitRoot, "AGENTS.md"), "root body");
        File.WriteAllText(Path.Combine(gitRoot, "a", "AGENTS.md"), "a body");

        var sources = AgentInstructionsLoader.Load(Path.Combine(gitRoot, "a"), userHome: _userHome.Root);

        Assert.Equal("a body", Assert.Single(sources).Content);
    }

    [Fact]
    public void Load_WithoutGitRoot_WalksToStartDirectoryParents()
    {
        var dir = Path.Combine(_root.Root, "plain", "nested");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_root.Root, "plain", "AGENTS.md"), "plain body");

        var sources = AgentInstructionsLoader.Load(dir, userHome: _userHome.Root);

        Assert.Equal("plain body", Assert.Single(sources).Content);
    }

    [Fact]
    public void Load_DeduplicatesSameFileAcrossScopes()
    {
        File.WriteAllText(UserFile, "shared body");
        // Workspace walk from the user .alcoler dir itself resolves to the same file.
        Directory.CreateDirectory(Path.Combine(UserFile, "..", ".git"));
        var workspaceRoot = Path.GetDirectoryName(UserFile)!;

        var sources = AgentInstructionsLoader.Load(workspaceRoot, userHome: _userHome.Root);

        var source = Assert.Single(sources);
        Assert.Equal(AgentInstructionScope.User, source.Scope);
        Assert.Equal("shared body", source.Content);
    }

    [Fact]
    public void Load_TruncatesAt100Kilobytes()
    {
        File.WriteAllText(UserFile, new string('a', 100 * 1024 + 5));

        var source = Assert.Single(AgentInstructionsLoader.Load(_root.Root, userHome: _userHome.Root));

        Assert.True(source.Truncated);
        Assert.Equal(100 * 1024, source.Content.Length);
    }

    [Fact]
    public void Load_Exact100Kilobytes_NotTruncated()
    {
        File.WriteAllText(UserFile, new string('a', 100 * 1024));

        var source = Assert.Single(AgentInstructionsLoader.Load(_root.Root, userHome: _userHome.Root));

        Assert.False(source.Truncated);
        Assert.Equal(100 * 1024, source.Content.Length);
    }

    [Fact]
    public void Load_DecodesUtf8Content()
    {
        File.WriteAllBytes(UserFile, Encoding.UTF8.GetBytes("héllo wörld"));

        var source = Assert.Single(AgentInstructionsLoader.Load(_root.Root, userHome: _userHome.Root));

        Assert.Equal("héllo wörld", source.Content);
    }

    [Fact]
    public void Load_NoFiles_ReturnsEmpty()
    {
        var workspace = Path.Combine(_root.Root, "ws");
        Directory.CreateDirectory(workspace);

        Assert.Empty(AgentInstructionsLoader.Load(workspace, userHome: _userHome.Root));
    }

    [Fact]
    public void Load_DirectoryNamedAgentsMd_Ignored()
    {
        var workspace = Path.Combine(_root.Root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(workspace, "AGENTS.md"));

        Assert.Empty(AgentInstructionsLoader.Load(workspace, userHome: _userHome.Root));
    }

    [Fact]
    public void Load_SetsFileNameFromPath()
    {
        File.WriteAllText(UserFile, "body");

        var source = Assert.Single(AgentInstructionsLoader.Load(_root.Root, userHome: _userHome.Root));

        Assert.Equal("AGENTS.md", source.FileName);
        Assert.Equal(UserFile, source.FilePath);
    }

    [Fact]
    public void Load_RendersThroughAgentsMdSection()
    {
        File.WriteAllText(UserFile, "user body");
        var workspace = Path.Combine(_root.Root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "ws body");

        var rendered = AgentsMdSection.Render(AgentInstructionsLoader.Load(workspace, userHome: _userHome.Root));

        Assert.StartsWith("# agentsMd\n", rendered);
        Assert.Contains($"Contents of {UserFile} (user default instructions):\n\nuser body", rendered);
        Assert.Contains($"Contents of {Path.Combine(workspace, "AGENTS.md")} (workspace instructions):\n\nws body", rendered);
        Assert.True(rendered!.IndexOf("(user default instructions)", StringComparison.Ordinal)
            < rendered.IndexOf("(workspace instructions)", StringComparison.Ordinal));
    }
}
