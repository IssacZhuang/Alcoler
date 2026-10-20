using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public sealed class SkillToolTests : IDisposable
{
    private readonly TempDirectorySkillProvider _provider = new();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task ValidateInput_RequiresNonEmptySkill()
    {
        var tool = new SkillTool();

        foreach (var json in new[]
        {
            "{}",
            """{"skill": ""}""",
            """{"skill": "   "}""",
            """{"skill": 42}""",
            """{"args": "no skill"}"""
        })
        {
            var issue = await tool.ValidateInputAsync(
                JsonDocument.Parse(json).RootElement, CancellationToken.None);
            Assert.NotNull(issue);
        }

        Assert.Null(await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"skill": "web-gui-tester"}""").RootElement,
            CancellationToken.None));
        Assert.Null(await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"skill": "web-gui-tester", "args": "login page"}""").RootElement,
            CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WithoutSkillProvider_ReportsUnavailable()
    {
        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill": "web-gui-tester"}""", skills: null), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Skill tool is not available in this session", result.SingleText());
    }

    [Fact]
    public async Task Execute_UnknownSkill_ReportsNotFound()
    {
        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill": "does-not-exist"}""", _provider), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "Skill 'does-not-exist' was not found. Use only skills from the available skills listing; do not guess names.",
            result.SingleText());
        Assert.Equal(["does-not-exist"], _provider.RequestedNames);
    }

    [Fact]
    public async Task Execute_RendersSkillContentWrappedWithBaseDirectory()
    {
        var baseDirectory = _provider.WriteSkill(
            "web-gui-tester",
            "# Web GUI Testing\nDrive the browser.");

        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill": "web-gui-tester"}""", _provider), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            $"""
            <skill_content name="web-gui-tester">
            # Skill: web-gui-tester
            # Web GUI Testing
            Drive the browser.
            Base directory for this skill: {baseDirectory}
            Relative paths in this skill are relative to this base directory.
            </skill_content>
            """,
            result.SingleText());
    }

    [Fact]
    public async Task Execute_TruncatesContentBeyondLimit()
    {
        _provider.WriteSkill(
            "big",
            new string('a', 100_000) + "TAIL-BEYOND-LIMIT");

        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill": "big"}""", _provider), CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.DoesNotContain("TAIL-BEYOND-LIMIT", text);
        Assert.EndsWith(
            "Relative paths in this skill are relative to this base directory.\n[Skill content truncated]\n</skill_content>",
            text);
        Assert.StartsWith(
            "<skill_content name=\"big\">\n# Skill: big\n" + new string('a', 100_000) + "\n",
            text);
    }

    [Fact]
    public void Metadata_IsReadOnlyWithoutApproval()
    {
        var metadata = new SkillTool().Metadata;

        Assert.True(metadata.ReadOnly);
        Assert.True(metadata.ConcurrentSafe);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.RequiresUserInteraction);
        Assert.Equal(SideEffectScope.None, metadata.SideEffectScope);
    }

    private static ToolExecutionContext Context(string json, ISkillContentProvider? skills) =>
        new(
            StrongId.NewSessionId(),
            @"C:\alcoler-test-workspace",
            JsonDocument.Parse(json).RootElement.Clone(),
            NoArtifactWorkspace.Instance,
            TimeProvider.System,
            Skills: skills);

    /// <summary>
    /// Backed by a real temp directory so BaseDirectory paths in results are real
    /// filesystem paths, like the production catalog.
    /// </summary>
    private sealed class TempDirectorySkillProvider : ISkillContentProvider, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "alcoler-skill-tests-" + Guid.NewGuid().ToString("N"));

        public List<string> RequestedNames { get; } = [];

        public string WriteSkill(string name, string markdown)
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), markdown);
            return directory;
        }

        public SkillDocument? TryLoad(string name, CancellationToken cancellationToken)
        {
            RequestedNames.Add(name);
            var directory = Path.Combine(_root, name);
            var skillPath = Path.Combine(directory, "SKILL.md");
            return File.Exists(skillPath)
                ? new SkillDocument(name, "loaded from the temp catalog", directory, File.ReadAllText(skillPath))
                : null;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
