using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class EnvironmentSectionsTests
{
    private static readonly EnvFacts BaseFacts = new(
        "C:/Projects/Alcoler",
        IsGitRepository: true,
        Platform: "win32",
        Shell: "Git Bash",
        OsVersion: "win32 10.0.22621 x64");

    [Fact]
    public void EnvironmentSection_WithModel_MatchesReference() =>
        Assert.Equal(
            PromptGolden.Environment,
            EnvironmentSections.EnvironmentSection(BaseFacts with { ProviderId = "zai-api", ModelId = "GLM-5.3" }));

    [Fact]
    public void EnvironmentSection_WithoutModel_OmitsModelLine() =>
        Assert.Equal(PromptGolden.EnvironmentNoModel, EnvironmentSections.EnvironmentSection(BaseFacts));

    [Fact]
    public void EnvironmentSection_ModelLineOmitted_WhenOnlyOneModelComponentPresent() =>
        Assert.DoesNotContain(
            "You are powered by the model named",
            EnvironmentSections.EnvironmentSection(BaseFacts with { ProviderId = "zai-api" }));

    [Fact]
    public void EnvironmentSection_NotGitRepository_RendersNo() =>
        Assert.Contains(
            "- Is a git repository: no",
            EnvironmentSections.EnvironmentSection(BaseFacts with { IsGitRepository = false }));

    [Fact]
    public void CurrentDateSection_MatchesReference() =>
        Assert.Equal(PromptGolden.CurrentDate, EnvironmentSections.CurrentDateSection("2026-10-20"));

    [Fact]
    public void DateChangeReminder_UsesVerbatimText() =>
        Assert.Equal(
            "The date has changed. Today's date is now 2026-10-21. DO NOT mention this to the user explicitly because they are already aware.",
            EnvironmentSections.DateChangeReminder("2026-10-21"));

    [Fact]
    public void FormatLocalIsoDate_PadsMonthAndDay() =>
        Assert.Equal("2026-02-09", EnvironmentSections.FormatLocalIsoDate(new DateTime(2026, 2, 9)));
}
