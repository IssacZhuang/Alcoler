using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Skills;
using Alcoler.Kernel.Tests.Storage;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class SkillsListingTests : IDisposable
{
    private readonly TempDirectory _workspaceRoot = new();
    private readonly TempDirectory _userRoot = new();

    public void Dispose()
    {
        _workspaceRoot.Dispose();
        _userRoot.Dispose();
    }

    private static SkillInfo Skill(
        string name,
        string description,
        string? whenToUse = null,
        string? alias = null,
        string? path = null) =>
        new(name, description, SkillSource.User, path ?? $"C:/s/{name}/SKILL.md", whenToUse, alias);

    [Fact]
    public void Render_MixedSkills_MatchesReference()
    {
        var skills = new List<SkillInfo>
        {
            Skill("deploy", "Ship the build."),
            Skill("report", "Research a topic and deliver a report.", whenToUse: "Use when the user wants research"),
            Skill("browser-use:control-browser", "Control a browser.", whenToUse: "Use for web UI", alias: "browser", path: "C:/s/b/SKILL.md"),
        };

        Assert.Equal(PromptGolden.Skills, SkillsListing.Render(skills));
    }

    [Fact]
    public void Render_DescriptionOver250Chars_MatchesReferenceTrim()
    {
        var skill = Skill("long", new string('x', 200) + new string('y', 100));

        Assert.Equal(PromptGolden.SkillsTrimmed, SkillsListing.Render([skill]));
    }

    [Fact]
    public void Render_DescriptionAtLimit_IsNotTrimmed()
    {
        var skill = Skill("exact", new string('x', 250));

        Assert.Equal(
            $"The following skills are available for use with the Skill tool:\n\n- exact: {new string('x', 250)} (file: C:/s/exact/SKILL.md)",
            SkillsListing.Render([skill]));
    }

    [Fact]
    public void Render_DescriptionOneOverLimit_TrimsTo249CharsPlusEllipsis()
    {
        var skill = Skill("over", new string('x', 251));

        Assert.Contains(new string('x', 249) + "... (file:", SkillsListing.Render([skill]));
        Assert.DoesNotContain(new string('x', 250), SkillsListing.Render([skill]));
    }

    [Fact]
    public void Render_TrimsCombinedDescriptionAndWhenToUse()
    {
        var skill = Skill("combined", new string('a', 240), whenToUse: new string('b', 100));

        var rendered = SkillsListing.Render([skill]);

        Assert.Contains(": " + new string('a', 240) + " - " + new string('b', 6) + "... (file:", rendered);
    }

    [Fact]
    public void Render_WithinBudget_ReturnsFullListing()
    {
        var skill = Skill("solo", "Does one thing.");
        var full = SkillsListing.Render([skill]);

        Assert.Equal(full, SkillsListing.Render([skill], metadataBudget: full!.Length));
        Assert.NotEqual(full, SkillsListing.Render([skill], metadataBudget: full.Length - 1));
    }

    [Fact]
    public void Render_OverBudget_FallsBackToNamesOnly()
    {
        var skills = new List<SkillInfo>
        {
            Skill("alpha", "Alpha skill.", whenToUse: "Use for alpha"),
            Skill("beta", "Beta skill.", alias: "b"),
        };

        var fallback = SkillsListing.Render(skills, metadataBudget: 10);

        Assert.Equal(
            "The following skills are available for use with the Skill tool:\n\n- alpha (file: C:/s/alpha/SKILL.md)\n- beta (also loadable as b) (file: C:/s/beta/SKILL.md)",
            fallback);
    }

    [Fact]
    public void Render_OverBudget_FallbackMatchesReference()
    {
        var skills = Enumerable.Range(0, 400)
            .Select(i => Skill(
                $"skill-{i}",
                $"Skill number {i} with some description text to consume budget."))
            .ToList();

        Assert.Equal(PromptGolden.SkillsFallback, SkillsListing.Render(skills, metadataBudget: 2000));
    }

    [Fact]
    public void Render_SortsByDisplayName()
    {
        var skills = new List<SkillInfo> { Skill("zeta", "z"), Skill("Alpha", "a"), Skill("beta", "b") };

        var rendered = SkillsListing.Render(skills);

        Assert.True(rendered!.IndexOf("Alpha", StringComparison.Ordinal) < rendered.IndexOf("beta", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("beta", StringComparison.Ordinal) < rendered.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_AliasEqualToName_OmitsAliasSuffix() =>
        Assert.DoesNotContain(
            "also loadable",
            SkillsListing.Render([Skill("dupe", "desc", alias: "dupe")]));

    [Fact]
    public void Render_EmptySkills_ReturnsNull() =>
        Assert.Null(SkillsListing.Render([]));

    [Fact]
    public void CatalogCapturesWhenToUse_FromFrontMatter()
    {
        var skillsDir = Path.Combine(_workspaceRoot.Root, ".alcoler", "skills", "report");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(
            Path.Combine(skillsDir, "SKILL.md"),
            """
            ---
            name: report
            description: Research a topic.
            when_to_use: Use when the user wants research
            ---

            Body.
            """);

        var catalog = new SkillsCatalog(userSkillsBase: _userRoot.Root);
        var skill = Assert.Single(catalog.Load(_workspaceRoot.Root));

        Assert.Equal("Use when the user wants research", skill.WhenToUse);
        Assert.StartsWith(
            "The following skills are available for use with the Skill tool:\n\n- report: Research a topic. - Use when the user wants research (file: ",
            SkillsListing.Render([skill]));
    }
}
