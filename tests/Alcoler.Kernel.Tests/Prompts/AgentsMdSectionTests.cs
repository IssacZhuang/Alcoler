using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class AgentsMdSectionTests
{
    [Fact]
    public void Render_UserThenWorkspace_MatchesReference()
    {
        var sources = new[]
        {
            new AgentInstructionSource(
                "C:/Users/u/.alcoler/AGENTS.md",
                "AGENTS.md",
                "user body",
                Truncated: true,
                AgentInstructionScope.User),
            new AgentInstructionSource(
                "C:/ws/AGENTS.md",
                "AGENTS.md",
                "ws body",
                Truncated: false,
                AgentInstructionScope.Workspace),
        };

        Assert.Equal(PromptGolden.AgentsMd, AgentsMdSection.Render(sources));
    }

    [Fact]
    public void Render_TruncatedSource_AppendsMarkerInsideBody()
    {
        var source = new AgentInstructionSource(
            "C:/ws/AGENTS.md", "AGENTS.md", "line", Truncated: true, AgentInstructionScope.Workspace);

        var rendered = AgentsMdSection.Render([source]);

        Assert.EndsWith("line\n\n[File truncated: AGENTS.md]", rendered);
    }

    [Fact]
    public void Render_TruncatedSourceWithWhitespaceBody_KeepsMarkerAsBody()
    {
        var source = new AgentInstructionSource(
            "C:/ws/AGENTS.md", "AGENTS.md", "   ", Truncated: true, AgentInstructionScope.Workspace);

        var rendered = AgentsMdSection.Render([source]);

        Assert.EndsWith(
            "Contents of C:/ws/AGENTS.md (workspace instructions):\n\n[File truncated: AGENTS.md]",
            rendered);
    }

    [Fact]
    public void Render_BodyIsTrimmed() =>
        Assert.EndsWith(
            "(workspace instructions):\n\ntrimmed",
            AgentsMdSection.Render(
            [
                new AgentInstructionSource("C:/ws/AGENTS.md", "AGENTS.md", "\ntrimmed\n", false, AgentInstructionScope.Workspace),
            ]));

    [Fact]
    public void Render_NoUsableSources_ReturnsNull() =>
        Assert.Null(AgentsMdSection.Render(
        [
            new AgentInstructionSource("C:/a/AGENTS.md", "AGENTS.md", "", false, AgentInstructionScope.Workspace),
        ]));
}
