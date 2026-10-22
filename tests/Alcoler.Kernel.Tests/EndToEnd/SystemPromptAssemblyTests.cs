using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class SystemPromptAssemblyTests
{
    private static ScriptStep Text(params string[] texts) => new ScriptStep.Events(
        [.. texts.Select(t => new ModelTextDelta(t)),
        new ModelStreamFinished(ModelFinishReason.Stop)]);

    [Fact]
    public async Task DefaultRequest_CarriesIdentityDynamicEnvironmentAndCurrentDate()
    {
        var request = await RunCapturedTurnAsync(Text("done"));

        var messages = request.Messages;
        var cli = Assert.IsType<InputTextBlock>(messages[0].Content[0]).Text;
        Assert.Equal(PromptTexts.CliPrefix, cli);
        Assert.NotNull(messages[0].CacheControl);

        var stable = Assert.IsType<InputTextBlock>(messages[1].Content[0]).Text;
        Assert.StartsWith("\nYou are an interactive Alcoler agent", stable);
        Assert.Contains("IMPORTANT: Assist with authorized security testing", stable);
        Assert.Contains("# Harness", stable);
        Assert.NotNull(messages[1].CacheControl);

        var dynamic = Assert.IsType<InputTextBlock>(messages[2].Content[0]).Text;
        Assert.StartsWith("\n\n# Communicating with the user", dynamic);
        Assert.Contains("# Code", dynamic);
        Assert.Contains("# Context management", dynamic);
        Assert.Contains("# Environment", dynamic);
        Assert.Contains("- Is a git repository: no", dynamic);
        Assert.Contains("- You are powered by the model named scripted/test-model.", dynamic);
        Assert.NotNull(messages[2].CacheControl);

        var meta = Assert.IsType<InputTextBlock>(messages[3].Content[0]).Text;
        Assert.StartsWith("<system-reminder>\nAs you answer the user's questions", meta);
        Assert.Contains("# currentDate\nToday's date is ", meta);
        Assert.Equal(MessageRole.User, messages[3].Role);
        Assert.Null(messages[3].CacheControl);
    }

    [Fact]
    public async Task WorkspaceAgentsMd_IsLoadedFromDiskIntoContextPrefix()
    {
        var request = await RunCapturedTurnAsync(
            Text("done"),
            configureHarness: harness =>
                File.WriteAllText(Path.Combine(harness.Root, "AGENTS.md"), "MARKER: workspace rules present"));

        var meta = request.Messages
            .Select(m => Assert.IsType<InputTextBlock>(m.Content[0]).Text)
            .First(text => text.Contains("As you answer the user's questions"));
        Assert.Contains("# agentsMd", meta);
        Assert.Contains("AGENTS.md (workspace instructions):", meta);
        Assert.Contains("MARKER: workspace rules present", meta);
    }

    [Fact]
    public async Task GitSnapshotProvider_InjectsSnapshotSectionInGitRepositories()
    {
        var request = await RunCapturedTurnAsync(
            Text("done"),
            configureHarness: harness => Directory.CreateDirectory(Path.Combine(harness.Root, ".git")),
            transform: options => options with
            {
                GitSnapshotProvider = _ => new GitSnapshot(
                    "feature/align",
                    "master",
                    "Tester",
                    "M src/Program.cs",
                    ["abc1234 opt: ui"]),
            });

        var dynamic = Assert.IsType<InputTextBlock>(request.Messages[2].Content[0]).Text;
        Assert.Contains("- Is a git repository: yes", dynamic);
        Assert.Contains("gitStatus: This is the git status at the start of the conversation", dynamic);
        Assert.Contains("Current branch: feature/align", dynamic);
        Assert.Contains("Main branch (you will usually use this for PRs): master", dynamic);
        Assert.Contains("Git user: Tester", dynamic);
        Assert.Contains("M src/Program.cs", dynamic);
        Assert.Contains("abc1234 opt: ui", dynamic);
    }

    [Fact]
    public async Task CustomSystemPrompt_ReplacesStableAndSuppressesDynamicSections()
    {
        var request = await RunCapturedTurnAsync(
            Text("done"),
            transform: options => options with { CustomSystemPrompt = "You are a focused child agent." });

        Assert.Equal(PromptTexts.CliPrefix, Assert.IsType<InputTextBlock>(request.Messages[0].Content[0]).Text);
        Assert.Equal("\nYou are a focused child agent.",
            Assert.IsType<InputTextBlock>(request.Messages[1].Content[0]).Text);
        Assert.NotEqual(MessageRole.System, request.Messages[2].Role);
        var allText = string.Join("\n", request.Messages.SelectMany(m => m.Content.OfType<InputTextBlock>()));
        Assert.DoesNotContain("# Communicating with the user", allText);
        Assert.DoesNotContain("# Environment", allText);
        Assert.Contains("# currentDate", allText);
    }

    [Fact]
    public async Task SkillsListingAndToolReloadMidSession_ObserveAddedRenamedAndDeletedSkills()
    {
        ScriptStep Load(string name, string id) => new ScriptStep.Events([
            new ModelToolCallReady(new ToolCall(new ToolCallId(id), "Skill",
                System.Text.Json.JsonSerializer.SerializeToElement(new { skill = name }))),
            new ModelStreamFinished(ModelFinishReason.ToolCalls),
        ]);
        await using var harness = TestHarness.Create(Load("first", "skill-first"), Text("loaded first"),
            Load("renamed", "skill-renamed"), Text("loaded renamed"), Text("after delete"));
        var skillDirectory = Path.Combine(harness.Root, ".alcoler", "skills", "fresh");
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Providers = [new ScriptedModelProvider(capturing)],
        });
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(skillPath, "---\nname: first\ndescription: First description\n---\n\nFIRST_BODY ${ZCODE_SKILL_DIR}\n");

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "load first"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, 2);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.Contains("- first: First description", PromptText(capturing.Requests[0]));
        Assert.Contains(PromptTexts.SkillSlashGuidance, PromptText(capturing.Requests[0]));
        Assert.Contains($"FIRST_BODY {skillDirectory}", ToolResultText(capturing.Requests[1]));

        File.WriteAllText(skillPath, "---\nname: renamed\ndescription: Updated description\n---\n\nSECOND_BODY ${CLAUDE_SKILL_DIR}\n");
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "load renamed"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, 4);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.Contains("- renamed: Updated description", PromptText(capturing.Requests[2]));
        Assert.DoesNotContain("- first: First description", PromptText(capturing.Requests[2]));
        Assert.Contains($"SECOND_BODY {skillDirectory}", ToolResultText(capturing.Requests[3]));

        File.Delete(skillPath);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "after delete"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, 5);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.DoesNotContain("- renamed: Updated description", PromptText(capturing.Requests[4]));
    }

    [Fact]
    public async Task UserAgentsSkills_AreListedAndLoadableThroughTheSkillTool()
    {
        ScriptStep Load(string name, string id) => new ScriptStep.Events([
            new ModelToolCallReady(new ToolCall(new ToolCallId(id), "Skill",
                System.Text.Json.JsonSerializer.SerializeToElement(new { skill = name }))),
            new ModelStreamFinished(ModelFinishReason.ToolCalls),
        ]);
        await using var harness = TestHarness.Create(Load("cross-agent", "skill-cross-agent"), Text("done"));
        var skillDirectory = Path.Combine(harness.Root, "user-skills-base", ".agents", "skills", "cross-agent");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: cross-agent\ndescription: Shared cross-agent skill\n---\n\nCROSS_AGENT_BODY\n");
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Providers = [new ScriptedModelProvider(capturing)],
        });
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "load cross-agent"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, 2);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.Contains("- cross-agent: Shared cross-agent skill", PromptText(capturing.Requests[0]));
        Assert.Contains("CROSS_AGENT_BODY", ToolResultText(capturing.Requests[1]));
    }

    [Fact]
    public async Task DisabledSkillPaths_HideListingAndRejectTheSkillTool()
    {
        ScriptStep Load(string name, string id) => new ScriptStep.Events([
            new ModelToolCallReady(new ToolCall(new ToolCallId(id), "Skill",
                System.Text.Json.JsonSerializer.SerializeToElement(new { skill = name }))),
            new ModelStreamFinished(ModelFinishReason.ToolCalls),
        ]);
        await using var harness = TestHarness.Create(Load("hidden-skill", "skill-hidden"), Text("done"));
        var skillDirectory = Path.Combine(
            harness.Root, "user-skills-base", ".agents", "skills", "hidden-skill");
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        File.WriteAllText(
            skillPath,
            "---\nname: hidden-skill\ndescription: Hidden skill\n---\n\nHIDDEN_BODY\n");
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Providers = [new ScriptedModelProvider(capturing)],
            DisabledSkillPathsProvider = () => [skillPath],
        });
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "load hidden-skill"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, 2);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.DoesNotContain("- hidden-skill: Hidden skill", PromptText(capturing.Requests[0]));
        Assert.DoesNotContain("HIDDEN_BODY", ToolResultText(capturing.Requests[1]));
        Assert.Contains("Skill not found: hidden-skill", ToolResultText(capturing.Requests[1]));
    }

    [Fact]
    public async Task ExplicitSkillsListingOverride_IsPreserved()
    {
        var request = await RunCapturedTurnAsync(Text("done"), transform: options => options with
        {
            SkillsListing = "HOST_LISTING_OVERRIDE",
        });

        Assert.Contains("HOST_LISTING_OVERRIDE", PromptText(request));
    }

    private static string ToolResultText(ModelRequest request) => string.Join("\n", request.Messages
        .SelectMany(message => message.Content.OfType<InputToolResultBlock>())
        .Select(result => result.Content));

    [Fact]
    public async Task DateRolloverMidSession_AppendsDateChangeReminder()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(8)));
        await using var harness = TestHarness.Create(Text("first answer"), Text("second answer"), Text("third answer"));
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Clock = clock,
            Providers = [new ScriptedModelProvider(capturing)],
        });
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "day one"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, expectedCount: 1);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.DoesNotContain("The date has changed", PromptText(capturing.Requests[0]));

        clock.Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(8));
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "day two"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, expectedCount: 2);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.Contains("The date has changed. Today's date is now 2026-10-21.", PromptText(capturing.Requests[^1]));
        Assert.Contains("DO NOT mention this to the user explicitly", PromptText(capturing.Requests[^1]));

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "same day again"), CancellationToken.None);
        await WaitForRequestsAsync(capturing, expectedCount: 3);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.Equal(1, PromptText(capturing.Requests[2]).Split("The date has changed.").Length - 1);
        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => p.Content.Contains("The date has changed", StringComparison.Ordinal));
    }

    private static async Task WaitForRequestsAsync(TestHarness.RequestCapturingModel capturing, int expectedCount)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (capturing.Requests.Count < expectedCount)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"Expected {expectedCount} captured requests, saw {capturing.Requests.Count}.");
            await Task.Delay(20);
        }
    }

    private static async Task<ModelRequest> RunCapturedTurnAsync(
        ScriptStep step,
        Action<TestHarness>? configureHarness = null,
        Func<AgentRuntimeOptions, AgentRuntimeOptions>? transform = null)
    {
        await using var harness = TestHarness.Create(step);
        configureHarness?.Invoke(harness);
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = harness.BuildRuntime(
            options => (transform?.Invoke(options) ?? options) with
            {
                Providers = [new ScriptedModelProvider(capturing)],
            });
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "hello"),
            CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        return Assert.Single(capturing.Requests);
    }

    private static string PromptText(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(m => m.Content.OfType<InputTextBlock>().Select(b => b.Text)));

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Phase != phase)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"Phase never reached {phase}.");
            await Task.Delay(20);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
