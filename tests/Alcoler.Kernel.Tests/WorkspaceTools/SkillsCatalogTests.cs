using Alcoler.Kernel.Skills;
using Alcoler.Kernel.Tests.Storage;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class SkillsCatalogTests : IDisposable
{
    private readonly TempDirectory _workspaceRoot = new();
    private readonly TempDirectory _userRoot = new();

    public void Dispose()
    {
        _workspaceRoot.Dispose();
        _userRoot.Dispose();
    }

    private string WorkspaceSkills => Path.Combine(_workspaceRoot.Root, ".alcoler", "skills");

    private string UserSkills => Path.Combine(_userRoot.Root, "skills");

    private void WriteSkill(string skillsRoot, string name, string content)
    {
        var directory = Path.Combine(skillsRoot, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private SkillsCatalog Catalog() => new(userSkillsRoot: UserSkills);

    [Fact]
    public void Load_ParsesFrontMatter()
    {
        WriteSkill(
            WorkspaceSkills,
            "report",
            """
            ---
            name: report
            description: "Research a topic and deliver a report."
            ---

            # Ignored Heading

            Body text.
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        var skill = Assert.Single(skills);
        Assert.Equal("report", skill.Name);
        Assert.Equal("Research a topic and deliver a report.", skill.Description);
        Assert.Equal(SkillSource.Workspace, skill.Source);
        Assert.EndsWith("SKILL.md", skill.SkPath);
    }

    [Fact]
    public void Load_FallsBackToHeadingAndParagraph_WithoutFrontMatter()
    {
        WriteSkill(
            UserSkills,
            "browser-control",
            """
            # Browser Control

            Drive a real browser to inspect and test web pages.

            Later paragraphs are not part of the description.
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        var skill = Assert.Single(skills);
        Assert.Equal("Browser Control", skill.Name);
        Assert.Equal("Drive a real browser to inspect and test web pages.", skill.Description);
        Assert.Equal(SkillSource.User, skill.Source);
    }

    [Fact]
    public void Load_WorkspaceSkillShadowsUserSkillWithSameName()
    {
        WriteSkill(
            WorkspaceSkills,
            "deploy",
            """
            ---
            name: deploy
            description: workspace deploy
            ---
            """);
        WriteSkill(
            UserSkills,
            "deploy",
            """
            ---
            name: deploy
            description: user deploy
            ---
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        var skill = Assert.Single(skills);
        Assert.Equal("workspace deploy", skill.Description);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public void Load_SkipsMalformedEntries()
    {
        Directory.CreateDirectory(Path.Combine(WorkspaceSkills, "no-skill-file"));
        WriteSkill(WorkspaceSkills, "empty", "");
        WriteSkill(WorkspaceSkills, "no-usable-metadata", "Just some prose without a heading or front matter.");
        WriteSkill(
            WorkspaceSkills,
            "healthy",
            """
            ---
            name: healthy
            description: works fine
            ---
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        var skill = Assert.Single(skills);
        Assert.Equal("healthy", skill.Name);
    }

    [Fact]
    public void Load_MissingRoots_ReturnEmpty()
    {
        Assert.Empty(Catalog().Load(_workspaceRoot.Root));
    }

    [Fact]
    public void RenderListing_ListsSkillsWithSource()
    {
        WriteSkill(
            WorkspaceSkills,
            "report",
            """
            ---
            name: report
            description: Research a topic.
            ---
            """);
        WriteSkill(
            UserSkills,
            "deploy",
            """
            ---
            name: deploy
            description: Ship the build.
            ---
            """);

        var listing = SkillsCatalog.RenderListing(Catalog().Load(_workspaceRoot.Root));

        Assert.StartsWith("Available skills:", listing);
        Assert.Contains("- report: Research a topic. (workspace)", listing);
        Assert.Contains("- deploy: Ship the build. (user)", listing);
    }

    [Fact]
    public void RenderListing_EmptyCatalog_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SkillsCatalog.RenderListing([]));
    }
}
