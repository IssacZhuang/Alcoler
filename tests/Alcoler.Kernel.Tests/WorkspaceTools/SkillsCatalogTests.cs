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

    private string WorkspaceAgentsSkills => Path.Combine(_workspaceRoot.Root, ".agents", "skills");

    private string UserSkills => Path.Combine(_userRoot.Root, ".alcoler", "skills");

    private string UserAgentsSkills => Path.Combine(_userRoot.Root, ".agents", "skills");

    private void WriteSkill(string skillsRoot, string name, string content)
    {
        var directory = Path.Combine(skillsRoot, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private SkillsCatalog Catalog() => new(userSkillsBase: _userRoot.Root);

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

    [Theory]
    [InlineData(">-", "Research a topic and deliver results.\nUse only for reports.")]
    [InlineData("|", "Research a topic\nand deliver results.\n\nUse only for reports.")]
    public void Load_ParsesMultilineMetadataAndBom(string style, string expected)
    {
        WriteSkill(WorkspaceSkills, "report", "\uFEFF---\nname: report\ndescription: " + style
            + "\n  Research a topic\n  and deliver results.\n\n  Use only for reports.\nwhen_to_use: >\n  When research\n  is requested\n---\nBody.");

        var skill = Assert.Single(Catalog().Load(_workspaceRoot.Root));

        Assert.Equal(expected, skill.Description);
        Assert.Equal("When research is requested", skill.WhenToUse);
        Assert.DoesNotContain("description:", SkillsCatalog.StripFrontMatterForTool(File.ReadAllText(skill.SkPath)));
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
    public void Load_MergesAgentsRootsAtBothLevels()
    {
        WriteSkill(
            WorkspaceAgentsSkills,
            "ws-agents",
            """
            ---
            name: ws-agents
            description: workspace agents skill
            ---
            """);
        WriteSkill(
            UserAgentsSkills,
            "user-agents",
            """
            ---
            name: user-agents
            description: user agents skill
            ---
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        Assert.Equal(
            [("ws-agents", SkillSource.Workspace), ("user-agents", SkillSource.User)],
            skills.Select(skill => (skill.Name, skill.Source)));
    }

    [Fact]
    public void Load_BrandRootShadowsAgentsRootWithSameNameWithinLevel()
    {
        WriteSkill(
            WorkspaceSkills,
            "deploy",
            """
            ---
            name: deploy
            description: workspace brand deploy
            ---
            """);
        WriteSkill(
            WorkspaceAgentsSkills,
            "deploy",
            """
            ---
            name: deploy
            description: workspace agents deploy
            ---
            """);
        WriteSkill(
            UserSkills,
            "report",
            """
            ---
            name: report
            description: user brand report
            ---
            """);
        WriteSkill(
            UserAgentsSkills,
            "report",
            """
            ---
            name: report
            description: user agents report
            ---
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        Assert.Equal(
            [("deploy", "workspace brand deploy"), ("report", "user brand report")],
            skills.Select(skill => (skill.Name, skill.Description)));
    }

    [Fact]
    public void Load_WorkspaceAgentsShadowsUserBrand_WithSameName()
    {
        WriteSkill(
            WorkspaceAgentsSkills,
            "deploy",
            """
            ---
            name: deploy
            description: workspace agents deploy
            ---
            """);
        WriteSkill(
            UserSkills,
            "deploy",
            """
            ---
            name: deploy
            description: user brand deploy
            ---
            """);

        var skills = Catalog().Load(_workspaceRoot.Root);

        var skill = Assert.Single(skills);
        Assert.Equal("workspace agents deploy", skill.Description);
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
    public void Load_ExcludesDisabledSkillPaths_WithPlatformPathComparison()
    {
        WriteSkill(WorkspaceSkills, "disabled", """
            ---
            name: disabled
            description: disabled skill
            ---
            """);
        WriteSkill(WorkspaceSkills, "kept", """
            ---
            name: kept
            description: kept skill
            ---
            """);
        var disabledPath = Path.Combine(WorkspaceSkills, "disabled", "SKILL.md");
        var recordedPath = OperatingSystem.IsWindows()
            ? disabledPath.Replace('\\', '/').ToUpperInvariant()
            : disabledPath.Replace('\\', '/');

        var skills = Catalog().Load(_workspaceRoot.Root, [recordedPath]);

        Assert.Equal(["kept"], skills.Select(skill => skill.Name));
    }

    [Fact]
    public void Load_DisabledPathOnlyAffectsTheWinningSkill()
    {
        WriteSkill(WorkspaceSkills, "deploy", """
            ---
            name: deploy
            description: workspace deploy
            ---
            """);
        WriteSkill(UserSkills, "deploy", """
            ---
            name: deploy
            description: user deploy
            ---
            """);

        var userPath = Path.Combine(UserSkills, "deploy", "SKILL.md");
        var skills = Catalog().Load(_workspaceRoot.Root, [userPath]);

        var skill = Assert.Single(skills);
        Assert.Equal("workspace deploy", skill.Description);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public void LoadUserSkills_ReturnsOnlyUserLevelAndHonorsDisabledPaths()
    {
        WriteSkill(WorkspaceSkills, "workspace-only", """
            ---
            name: workspace-only
            description: workspace skill
            ---
            """);
        WriteSkill(UserSkills, "user-brand", """
            ---
            name: user-brand
            description: user brand skill
            ---
            """);
        WriteSkill(UserAgentsSkills, "user-agents", """
            ---
            name: user-agents
            description: user agents skill
            ---
            """);
        var disabledPath = Path.Combine(UserAgentsSkills, "user-agents", "SKILL.md");

        var skills = Catalog().LoadUserSkills([disabledPath]);

        var skill = Assert.Single(skills);
        Assert.Equal("user-brand", skill.Name);
        Assert.Equal(SkillSource.User, skill.Source);
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
