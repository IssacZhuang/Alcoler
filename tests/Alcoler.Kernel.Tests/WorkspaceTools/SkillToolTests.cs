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
            """{"name": ""}""",
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
        Assert.Null(await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"name": "web-gui-tester"}""").RootElement,
            CancellationToken.None));
    }

    [Fact]
    public async Task ResolveInput_RewritesLegacyNameToSkill()
    {
        var tool = new SkillTool();

        var resolved = await tool.ResolveInputAsync(
            JsonDocument.Parse("""{"name": "web-gui-tester", "args": "login page"}""").RootElement,
            CancellationToken.None);

        Assert.Equal("web-gui-tester", resolved.GetProperty("skill").GetString());
        Assert.Equal("login page", resolved.GetProperty("args").GetString());
        Assert.False(resolved.TryGetProperty("name", out _));

        var unchanged = await tool.ResolveInputAsync(
            JsonDocument.Parse("""{"skill": "web-gui-tester", "args": "login page"}""").RootElement,
            CancellationToken.None);
        Assert.Equal("web-gui-tester", unchanged.GetProperty("skill").GetString());
        Assert.False(unchanged.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Execute_LegacyNameInput_LoadsTheSkill()
    {
        _provider.WriteSkill("legacy-skill", "# Legacy\nOld input shape.");

        var result = await new SkillTool().ExecuteAsync(
            Context("""{"name": "legacy-skill", "args": "anything"}""", _provider),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("<skill_content name=\"legacy-skill\">", result.SingleText());
        Assert.Equal(["legacy-skill"], _provider.RequestedNames);
    }

    [Fact]
    public async Task Executor_AcceptsLegacyNameWithoutAdvertisingIt()
    {
        _provider.WriteSkill("legacy-skill", "Legacy body.");
        var tool = new SkillTool();
        var executor = new ToolCallExecutor(new ToolRegistry([tool]), new ScriptedPermissionGate());
        var input = JsonDocument.Parse("""{"name":"legacy-skill"}""").RootElement.Clone();
        var outcome = await executor.ExecuteAsync(
            new Alcoler.Contracts.Models.ToolCall(StrongId.NewToolCallId(), "Skill", input),
            (call, _) => ValueTask.FromResult(Context(call.Input.GetRawText(), _provider)),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.False(outcome.Result.IsError);
        Assert.Contains("Legacy body.", outcome.Result.SingleText());
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
        Assert.Equal("Skill not found: does-not-exist", result.SingleText());
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
    public async Task Execute_ExpandsOnlySupportedBaseDirectoryVariables()
    {
        var directory = _provider.WriteSkill("paths",
            "${CLAUDE_SKILL_DIR}/script.py\n${ZCODE_SKILL_DIR}/config.json\n${CLAUDE_SKILL_DIR}\n$ARGUMENTS ${OTHER}");

        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill":"paths","args":"not substituted"}""", _provider), CancellationToken.None);

        Assert.Contains($"{directory}/script.py\n{directory}/config.json\n{directory}\n$ARGUMENTS ${{OTHER}}", result.SingleText());
        Assert.DoesNotContain("not substituted", result.SingleText());
    }

    [Fact]
    public async Task Execute_TruncatesUtf8BytesBeforeExpandingVariables()
    {
        _provider.WriteSkill("unicode", new string('界', 33_333) + "xTAIL");

        var result = await new SkillTool().ExecuteAsync(
            Context("""{"skill":"unicode"}""", _provider), CancellationToken.None);

        Assert.Contains(new string('界', 33_333) + "x\n", result.SingleText());
        Assert.DoesNotContain("TAIL", result.SingleText());
        Assert.Contains("[Skill content truncated]", result.SingleText());
    }

    [Fact]
    public void Contract_AdvertisesCurrentInputOnlyAndCurrentTurnLoadedGuidance()
    {
        var contract = new SkillTool().Contract;
        Assert.Equal(["skill"], contract.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.False(contract.InputSchema.GetProperty("properties").TryGetProperty("name", out _));
        Assert.Contains("fully qualified `plugin:skill`", contract.Description);
        Assert.Contains("<command-name> tag in the current conversation turn", contract.Description);
        Assert.DoesNotContain("already been loaded in this conversation", contract.Description);
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
