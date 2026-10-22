using Alcoler.Kernel.Skills;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class ComposerSkillCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alcoler-composer-skills-" + Guid.NewGuid().ToString("N"));

    public ComposerSkillCatalogTests() => Directory.CreateDirectory(_directory);

    private string WorkspaceRoot => Path.Combine(_directory, "workspace");

    private string WorkspaceSkills => Path.Combine(WorkspaceRoot, ".alcoler", "skills");

    private string UserBase => Path.Combine(_directory, "user");

    private string UserSkills => Path.Combine(UserBase, ".alcoler", "skills");

    private ComposerSkillCatalog Catalog() => new(new SkillsCatalog(userSkillsBase: UserBase));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string WriteSkillText(string root, string directoryName, string content)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "SKILL.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteSkill(string root, string name, string description, string? directoryName = null) =>
        WriteSkillText(root, directoryName ?? name, $"---\nname: {name}\ndescription: {description}\n---\nSkill instructions.");

    [Fact]
    public void GetEntries_ProjectsScannerNamesDescriptionsAndPathsInDeterministicOrder()
    {
        var zeta = WriteSkill(WorkspaceSkills, "zeta", "Last workspace skill.");
        var alpha = WriteSkill(UserSkills, "Alpha", "First user skill.", "folder-name-is-not-command");
        var beta = WriteSkill(WorkspaceSkills, "beta", "Middle workspace skill.");

        var entries = Catalog().GetEntries(WorkspaceRoot);

        Assert.Equal(
        [
            new ComposerSkillEntry("Alpha", "First user skill.", alpha),
            new ComposerSkillEntry("beta", "Middle workspace skill.", beta),
            new ComposerSkillEntry("zeta", "Last workspace skill.", zeta),
        ], entries);
    }

    [Fact]
    public void GetEntries_ExcludesDisabledSkillPaths()
    {
        var kept = WriteSkill(WorkspaceSkills, "kept", "Kept skill.");
        var disabled = WriteSkill(UserSkills, "disabled", "Disabled skill.");

        var entries = Catalog().GetEntries(WorkspaceRoot, [disabled]);

        Assert.Equal([new ComposerSkillEntry("kept", "Kept skill.", kept)], entries);
        Assert.Empty(Catalog().Query(WorkspaceRoot, "/disabled", [disabled]));
        Assert.Single(Catalog().Query(WorkspaceRoot, "/kept", [disabled]));
    }

    [Fact]
    public void GetEntries_ReusesCaseInsensitiveWorkspacePrecedenceFromKernelCatalog()
    {
        WriteSkill(UserSkills, "deploy", "User deployment.");
        var workspacePath = WriteSkill(WorkspaceSkills, "DEPLOY", "Workspace deployment.");
        var scanner = new SkillsCatalog(userSkillsBase: UserBase);

        var entry = Assert.Single(new ComposerSkillCatalog(scanner).GetEntries(WorkspaceRoot));
        var kernelEntry = Assert.Single(scanner.Load(WorkspaceRoot));

        Assert.Equal(new ComposerSkillEntry("DEPLOY", "Workspace deployment.", workspacePath), entry);
        Assert.Equal(kernelEntry.Name, entry.Name);
        Assert.Equal(kernelEntry.Description, entry.Description);
        Assert.Equal(kernelEntry.SkPath, entry.Path);
    }

    [Fact]
    public void GetEntries_ReusesFrontMatterAndHeadingFallbackWithoutReadingBodiesIntoEntries()
    {
        WriteSkillText(WorkspaceSkills, "report", "\uFEFF---\nname: report\ndescription: >-\n  Research topics\n  and compare findings.\n---\nPrivate skill body.");
        var headingPath = WriteSkillText(UserSkills, "fallback", "# browser\n\nInspect a browser.\n\nAnother paragraph.");

        var entries = Catalog().GetEntries(WorkspaceRoot);

        Assert.Equal(
        [
            new ComposerSkillEntry("browser", "Inspect a browser.", headingPath),
            new ComposerSkillEntry("report", "Research topics and compare findings.", Path.Combine(WorkspaceSkills, "report", "SKILL.md")),
        ], entries);
    }

    [Fact]
    public void GetEntries_SkipsMalformedSkillsAsTheKernelDoes()
    {
        WriteSkillText(WorkspaceSkills, "empty", "");
        WriteSkillText(UserSkills, "missing-description", "---\nname: missing-description\n---\nNo heading or description.");
        WriteSkill(WorkspaceSkills, "usable", "A valid skill.");
        Directory.CreateDirectory(Path.Combine(WorkspaceSkills, "missing-file"));

        Assert.Equal("usable", Assert.Single(Catalog().GetEntries(WorkspaceRoot)).Name);
    }

    [Fact]
    public void GetEntries_MissingWorkspaceCanStillShowInjectedUserSkills()
    {
        WriteSkill(UserSkills, "report", "User report.");

        Assert.Equal("report", Assert.Single(Catalog().GetEntries(WorkspaceRoot)).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void GetEntries_NoWorkspace_ReturnsNoEntries(string root)
    {
        WriteSkill(UserSkills, "report", "User report.");

        Assert.Empty(Catalog().GetEntries(root));
    }

    [Fact]
    public void GetEntries_EmptyRoots_ReturnNoEntries()
    {
        Assert.Empty(Catalog().GetEntries(WorkspaceRoot));
    }

    [Fact]
    public void GetEntries_RefreshesAdditionsEditsAndRemovalsWithoutAStaleCache()
    {
        var catalog = Catalog();
        Assert.Empty(catalog.GetEntries(WorkspaceRoot));
        var path = WriteSkill(WorkspaceSkills, "report", "Original report.");
        Assert.Equal("Original report.", Assert.Single(catalog.GetEntries(WorkspaceRoot)).Description);

        WriteSkill(WorkspaceSkills, "report", "Updated report.");
        Assert.Equal("Updated report.", Assert.Single(catalog.GetEntries(WorkspaceRoot)).Description);

        File.Delete(path);
        Assert.Empty(catalog.GetEntries(WorkspaceRoot));
    }

    [Fact]
    public void GetEntries_DoesNotLeakSkillsAcrossWorkspaces()
    {
        WriteSkill(WorkspaceSkills, "local", "Local skill.");
        WriteSkill(UserSkills, "global", "Global skill.");
        var catalog = Catalog();

        Assert.Equal(["global", "local"], catalog.GetEntries(WorkspaceRoot).Select(entry => entry.Name));
        Assert.Equal(["global"], catalog.GetEntries(Path.Combine(_directory, "another-workspace")).Select(entry => entry.Name));
    }

    [Theory]
    [InlineData("/", new[] { "deploy", "report", "review" })]
    [InlineData("/re", new[] { "report", "review" })]
    [InlineData("/RE", new[] { "report", "review" })]
    [InlineData("/port", new[] { "report" })]
    [InlineData("/report", new[] { "report" })]
    [InlineData("/missing", new string[0])]
    [InlineData("/Research", new string[0])]
    [InlineData("report", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(" /re", new string[0])]
    [InlineData("Please /re", new string[0])]
    [InlineData("/report ", new string[0])]
    [InlineData("/report arguments", new string[0])]
    [InlineData("/re\n", new string[0])]
    [InlineData("/re\t", new string[0])]
    public void Query_MatchesOnlySlashCommandTokensByName(string text, string[] expectedNames)
    {
        WriteSkill(WorkspaceSkills, "report", "Research topics.");
        WriteSkill(UserSkills, "review", "Inspect changes.");
        WriteSkill(UserSkills, "deploy", "Ship changes.");

        Assert.Equal(expectedNames, Catalog().Query(WorkspaceRoot, text).Select(entry => entry.Name));
    }

    [Fact]
    public void Query_PreservesExactCommandNameForOrdinaryUserTextInvocation()
    {
        WriteSkill(WorkspaceSkills, "CodeReview", "Review code.", "different-directory");

        var entry = Assert.Single(Catalog().Query(WorkspaceRoot, "/coderev"));

        Assert.Equal("/CodeReview examine this diff", "/" + entry.Name + " examine this diff");
        Assert.Equal("CodeReview", Assert.Single(new SkillsCatalog(userSkillsBase: UserBase).Load(WorkspaceRoot)).Name);
    }
}
