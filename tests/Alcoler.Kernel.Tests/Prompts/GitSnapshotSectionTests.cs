using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class GitSnapshotSectionTests
{
    [Fact]
    public void Render_FullSnapshot_MatchesReference()
    {
        var snapshot = new GitSnapshot(
            "feature/prompts",
            "main",
            "alcoler-dev",
            " M src/Kernel.cs\nA  tests/New.cs",
            ["abc123 last", "def456 prev"]);

        Assert.Equal(PromptGolden.GitSystemContext, GitSnapshotSection.Render(snapshot, "win32"));
    }

    [Fact]
    public void Render_MinimalSnapshot_MatchesReference()
    {
        var snapshot = new GitSnapshot(null, null, null, "", []);

        Assert.Equal(PromptGolden.GitSystemContextMinimal, GitSnapshotSection.Render(snapshot, "win32"));
    }

    [Fact]
    public void Render_UnknownStatus_RendersUnknownMarker()
    {
        var snapshot = new GitSnapshot("feature/prompts", "main", "alcoler-dev", null, ["abc123 last", "def456 prev"]);

        Assert.Equal(PromptGolden.GitSystemContextUnknown, GitSnapshotSection.Render(snapshot, "win32"));
    }

    [Fact]
    public void Render_EmptyBranchFields_OmitTheirLines()
    {
        var snapshot = new GitSnapshot("", null, "", " M file", []);

        var rendered = GitSnapshotSection.Render(snapshot, "win32");

        Assert.DoesNotContain("Current branch", rendered);
        Assert.DoesNotContain("Main branch", rendered);
        Assert.DoesNotContain("Git user", rendered);
    }

    [Fact]
    public void Render_StatusAtLimit_IsNotTruncated()
    {
        var status = new string('M', 2000);
        var snapshot = new GitSnapshot(null, null, null, status, []);

        var rendered = GitSnapshotSection.Render(snapshot, "win32");

        Assert.DoesNotContain("truncated because", rendered);
        Assert.Contains(status, rendered);
    }

    [Theory]
    [InlineData("win32", "PowerShell")]
    [InlineData("linux", "Bash")]
    [InlineData("darwin", "Bash")]
    public void Render_StatusOverLimit_TruncatesWithPlatformToolName(string platform, string tool)
    {
        var snapshot = new GitSnapshot(null, null, null, new string('M', 2001), []);

        var rendered = GitSnapshotSection.Render(snapshot, platform);

        var truncatedStatus = new string('M', 2000)
            + "\n... (truncated because it exceeds 2k characters. If you need more information, run \"git status\" using "
            + tool + ")";
        Assert.Equal(
            GitSnapshotSection.Header + "\n\nStatus:\n" + truncatedStatus + "\n\nRecent commits:\n",
            rendered);
    }

    [Fact]
    public void Render_CrlfStatusLines_AreSplitAndEmptyLinesDropped()
    {
        var snapshot = new GitSnapshot(null, null, null, " M a\r\n\r\n?? b\r\n", []);

        Assert.Contains("Status:\n M a\n?? b", GitSnapshotSection.Render(snapshot, "win32"));
    }

    [Fact]
    public void Render_EmptyStatusLines_RendersClean()
    {
        var snapshot = new GitSnapshot(null, null, null, "\n\n", []);

        Assert.Contains("Status:\n(clean)", GitSnapshotSection.Render(snapshot, "win32"));
    }

    [Fact]
    public void StatusToolName_MapsPlatform()
    {
        Assert.Equal("PowerShell", GitSnapshotSection.StatusToolName("win32"));
        Assert.Equal("Bash", GitSnapshotSection.StatusToolName("linux"));
    }
}
