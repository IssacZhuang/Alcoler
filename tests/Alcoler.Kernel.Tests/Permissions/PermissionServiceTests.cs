using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Permissions;
using Xunit;

namespace Alcoler.Kernel.Tests.Permissions;

public class PermissionServiceTests
{
    private static readonly SessionId Session = StrongId.NewSessionId();

    private static JsonElement Input(object? value) => JsonSerializer.SerializeToElement(value ?? new { });

    private static ToolMetadata Tool(
        bool readOnly = false,
        bool destructive = false,
        bool requiresUserInteraction = false,
        SideEffectScope scope = SideEffectScope.None,
        RiskLevel risk = RiskLevel.Low,
        bool needsApproval = false,
        bool allowedInPlanMode = false) =>
        new(readOnly, destructive, ConcurrentSafe: true, requiresUserInteraction, scope, risk, needsApproval, allowedInPlanMode);

    private static ToolMetadata Preset(string name) => name switch
    {
        "readOnlyLow" => Tool(readOnly: true, scope: SideEffectScope.None, risk: RiskLevel.Low, allowedInPlanMode: true),
        "readOnlyNetworkLow" => Tool(readOnly: true, scope: SideEffectScope.Network, risk: RiskLevel.Low, allowedInPlanMode: true),
        "readOnlyMediumRisk" => Tool(readOnly: true, scope: SideEffectScope.None, risk: RiskLevel.Medium, allowedInPlanMode: true),
        "bashNetwork" => Tool(scope: SideEffectScope.Network, risk: RiskLevel.Medium),
        "workspaceWrite" => Tool(scope: SideEffectScope.Workspace, risk: RiskLevel.Medium),
        "destructive" => Tool(destructive: true, scope: SideEffectScope.Workspace, risk: RiskLevel.Medium),
        "highRiskSystem" => Tool(scope: SideEffectScope.System, risk: RiskLevel.High),
        "needsApproval" => Tool(needsApproval: true, scope: SideEffectScope.System, risk: RiskLevel.Medium),
        "noneScopeSideEffect" => Tool(scope: SideEffectScope.None, risk: RiskLevel.Medium, allowedInPlanMode: true),
        "workspacePlanAllowed" => Tool(scope: SideEffectScope.Workspace, risk: RiskLevel.Medium, allowedInPlanMode: true),
        "readOnlySession" => Tool(readOnly: true, scope: SideEffectScope.Session, risk: RiskLevel.Low),
        "sessionPlanAllowed" => Tool(scope: SideEffectScope.Session, risk: RiskLevel.Low, allowedInPlanMode: true),
        "destructivePlanAllowed" => Tool(destructive: true, scope: SideEffectScope.Session, risk: RiskLevel.High, allowedInPlanMode: true),
        "userInteraction" => Tool(requiresUserInteraction: true, readOnly: true, scope: SideEffectScope.UserInteraction, risk: RiskLevel.Low, allowedInPlanMode: true),
        "userInteractionApproval" => Tool(requiresUserInteraction: true, readOnly: true, scope: SideEffectScope.UserInteraction, risk: RiskLevel.Low, needsApproval: true, allowedInPlanMode: true),
        _ => throw new ArgumentException($"unknown tool preset: {name}", nameof(name)),
    };

    private static PermissionCheckRequest Request(
        string toolName,
        ToolMetadata tool,
        object? input = null,
        PermissionMode mode = PermissionMode.Build,
        bool planEnabled = false) =>
        new(Session, toolName, Input(input), tool, mode, PlanEnabled: planEnabled);

    private static PermissionRule Allow(string toolName, string? content) => new(toolName, content, PermissionVerdict.Allow);

    private static PermissionRule Ask(string toolName, string? content) => new(toolName, content, PermissionVerdict.Ask);

    private static PermissionRule Deny(string toolName, string? content) => new(toolName, content, PermissionVerdict.Deny);

    private sealed class StubBroker : IPermissionBroker
    {
        public PermissionDecision Next { get; set; } = PermissionDecision.Allow;

        public int Calls { get; private set; }

        public List<PermissionRequest> Requests { get; } = [];

        public ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            return ValueTask.FromResult(Next);
        }
    }

    private sealed class NeverDecideBroker : IPermissionBroker
    {
        public async ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return PermissionDecision.Allow;
        }
    }

    [Theory]
    [InlineData(PermissionMode.Build, "readOnlyLow", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Build, "readOnlyNetworkLow", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Build, "readOnlyMediumRisk", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Build, "bashNetwork", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Build, "workspaceWrite", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Build, "destructive", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Build, "highRiskSystem", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Build, "needsApproval", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Edit, "readOnlyLow", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Edit, "bashNetwork", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Edit, "destructive", PermissionOutcome.Allowed, true, false)]
    [InlineData(PermissionMode.Edit, "workspaceWrite", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Yolo, "readOnlyLow", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "readOnlyNetworkLow", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "noneScopeSideEffect", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "readOnlySession", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "sessionPlanAllowed", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "workspacePlanAllowed", PermissionOutcome.Allowed, false, true)]
    [InlineData(PermissionMode.Yolo, "workspaceWrite", PermissionOutcome.Denied, false, true)]
    [InlineData(PermissionMode.Yolo, "destructivePlanAllowed", PermissionOutcome.Denied, false, true)]
    [InlineData(PermissionMode.Yolo, "bashNetwork", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Yolo, "workspaceWrite", PermissionOutcome.Allowed, false, false)]
    [InlineData(PermissionMode.Yolo, "destructive", PermissionOutcome.Allowed, false, false)]
    public async Task ModeDefaults_FollowPrecedenceChain(
        PermissionMode mode,
        string preset,
        PermissionOutcome expected,
        bool expectBrokerCall,
        bool planEnabled)
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, mode, planEnabled: planEnabled);

        var decision = await service.CheckAsync(
            Request("Bash", Preset(preset), mode: mode, planEnabled: planEnabled), CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
        Assert.Equal(expectBrokerCall ? 1 : 0, broker.Calls);
    }

    [Fact]
    public async Task PlanMode_TransitionPolicy_DeniesToolDisallowedInPlanMode()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo, planEnabled: true);

        var decision = await service.CheckAsync(
            Request("Write", Preset("workspaceWrite"), mode: PermissionMode.Yolo, planEnabled: true),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task PlanMode_AllowsReadOnlyBashInput()
    {
        var broker = new StubBroker();
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo, planEnabled: true);
        var request = new PermissionCheckRequest(
            Session,
            "Bash",
            Input(new { command = "git status" }),
            Preset("bashNetwork"),
            PermissionMode.Yolo,
            TreatAsReadOnly: true,
            PlanEnabled: true);

        var decision = await service.CheckAsync(request, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ExitPlanMode_OutsidePlanMode_DeniesWithoutAsking()
    {
        var broker = new StubBroker();
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Edit);
        var request = new PermissionCheckRequest(
            Session,
            "ExitPlanMode",
            Input(new { plan = "do it" }),
            Preset("userInteractionApproval"),
            PermissionMode.Edit);

        var decision = await service.CheckAsync(request, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ExitPlanMode_InsidePlanMode_AsksWithFeedbackEnabled()
    {
        var broker = new StubBroker();
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo, planEnabled: true);
        var request = new PermissionCheckRequest(
            Session,
            "ExitPlanMode",
            Input(new { plan = "do it" }),
            Preset("userInteractionApproval"),
            PermissionMode.Yolo,
            PlanEnabled: true);

        var decision = await service.CheckAsync(request, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.True(Assert.Single(broker.Requests).AllowsFeedback);
    }

    [Fact]
    public async Task UserInteractionApproval_AsksEvenInYolo()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo);

        var decision = await service.CheckAsync(
            Request("InteractiveApprovalTool", Preset("userInteractionApproval"), mode: PermissionMode.Yolo),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(1, broker.Calls);
        Assert.Contains("user interaction", broker.Requests[0].Reason);
    }

    [Theory]
    [InlineData(PermissionMode.Build, false)]
    [InlineData(PermissionMode.Edit, false)]
    [InlineData(PermissionMode.Yolo, false)]
    [InlineData(PermissionMode.Build, true)]
    public async Task AskUserQuestion_IsAllowedWithoutPermissionBroker(PermissionMode mode, bool planEnabled)
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var service = new PermissionService(broker, PermissionRuleset.Empty, mode, planEnabled: planEnabled);

        var decision = await service.CheckAsync(
            Request("AskUserQuestion", Preset("userInteraction"), mode: mode, planEnabled: planEnabled),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task SessionAskRule_SurvivesYoloBypass()
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo);
        service.GrantSessionPermission(Ask("Bash", "git push:*"));

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "git push --force" }, PermissionMode.Yolo),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(1, broker.Calls);
        Assert.Contains("session ask rule", broker.Requests[0].Reason);
    }

    [Fact]
    public async Task SessionAskRule_PrecedesProjectDenyRule()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var rules = new PermissionRuleset([], [], [Deny("Bash", null)]);
        var service = new PermissionService(broker, rules, PermissionMode.Build);
        service.GrantSessionPermission(Ask("Bash", "git:*"));

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "git status" }),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task ProjectDenyRule_SurvivesYoloBypass()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var rules = new PermissionRuleset([], [], [Deny("Bash", "curl:*")]);
        var service = new PermissionService(broker, rules, PermissionMode.Yolo);

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "curl http://x" }, PermissionMode.Yolo),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task SessionDenyRule_DeniesWithoutAsking()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo);
        service.GrantSessionPermission(Deny("Bash", null));

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "ls" }, PermissionMode.Yolo),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ProjectAskRule_BeatsProjectAllowRule()
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var rules = new PermissionRuleset([Allow("Bash", null)], [Ask("Bash", "git push:*")], []);
        var service = new PermissionService(broker, rules, PermissionMode.Build);

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "git push" }),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(1, broker.Calls);
        Assert.Contains("project ask rule", broker.Requests[0].Reason);
    }

    [Fact]
    public async Task SessionAllowRule_OverridesProjectAskRule()
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var rules = new PermissionRuleset([], [Ask("Bash", null)], []);
        var service = new PermissionService(broker, rules, PermissionMode.Build);
        service.GrantSessionPermission(Allow("Bash", "git status"));

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "git status" }),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ProjectAllowRule_CannotBypassPlanMode()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var rules = new PermissionRuleset([Allow("Write", null)], [], []);
        var service = new PermissionService(broker, rules, PermissionMode.Yolo, planEnabled: true);

        var decision = await service.CheckAsync(
            Request("Write", Preset("workspaceWrite"), mode: PermissionMode.Yolo, planEnabled: true),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ProjectAskRule_CannotBypassPlanMode()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var rules = new PermissionRuleset([], [Ask("Write", null)], []);
        var service = new PermissionService(broker, rules, PermissionMode.Yolo, planEnabled: true);

        var decision = await service.CheckAsync(
            Request("Write", Preset("workspaceWrite"), mode: PermissionMode.Yolo, planEnabled: true),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ConfigDisallowed_DeniesEvenInYolo()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Yolo, disallowedTools: ["WebFetch"]);

        var decision = await service.CheckAsync(
            Request("WebFetch", Preset("readOnlyNetworkLow"), new { url = "https://x" }, PermissionMode.Yolo),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task GrantSessionPermission_AppliesToSubsequentChecks()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build);
        var input = new { command = "git status" };
        var request = Request("Bash", Preset("bashNetwork"), input);

        var first = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, first.Outcome);
        Assert.Equal(1, broker.Calls);

        service.GrantSessionPermission(Allow("Bash", "git status"));

        var second = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, second.Outcome);
        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task UpdateProjectRules_SwapsRulesetForNextCheck()
    {
        var broker = new StubBroker { Next = PermissionDecision.Allow };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build);
        var request = Request("Bash", Preset("bashNetwork"), new { command = "git status" });

        var first = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, first.Outcome);
        Assert.Equal(1, broker.Calls);

        service.UpdateProjectRules(new PermissionRuleset([Allow("Bash", null)], [], []));

        var second = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, second.Outcome);
        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task SetMode_AppliesToNextCheck()
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build);
        var request = Request("Bash", Preset("bashNetwork"), new { command = "curl http://x" });

        var first = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Denied, first.Outcome);
        Assert.Equal(1, broker.Calls);

        service.SetMode(PermissionMode.Yolo);

        var second = await service.CheckAsync(request, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, second.Outcome);
        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task AskFlow_BuildsCompletePermissionRequest()
    {
        var broker = new StubBroker { Next = PermissionDecision.Deny };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build);
        var toolCallId = StrongId.NewToolCallId();

        var decision = await service.CheckAsync(
            Request("Bash", Preset("destructive"), new { command = "rm -rf build" }) with { ToolCallId = toolCallId },
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        var request = Assert.Single(broker.Requests);
        Assert.Equal("Bash", request.ToolName);
        Assert.Equal(Session, request.SessionId);
        Assert.Equal(RiskLevel.Medium, request.RiskLevel);
        Assert.Equal("rm -rf build", request.Input.GetProperty("command").GetString());
        Assert.NotEqual(default, request.InteractionId);
        Assert.Equal(toolCallId, request.ToolCallId);
        Assert.False(string.IsNullOrEmpty(request.Reason));
    }

    [Fact]
    public async Task BrokerDecision_Modified_PassesThroughWithModifiedInput()
    {
        var modified = Input(new { command = "git status --short" });
        var broker = new StubBroker { Next = new PermissionDecision(PermissionOutcome.Modified, modified) };
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build);

        var decision = await service.CheckAsync(
            Request("Bash", Preset("bashNetwork"), new { command = "git status" }),
            CancellationToken.None);

        Assert.Equal(PermissionOutcome.Modified, decision.Outcome);
        Assert.Equal("git status --short", decision.ModifiedInput!.Value.GetProperty("command").GetString());
    }

    [Fact]
    public async Task BrokerTimeout_DeniesInsteadOfHanging()
    {
        var time = new ManualTimeProvider();
        var broker = new NeverDecideBroker();
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build, timeProvider: time);

        var pending = service.CheckAsync(Request("Bash", Preset("bashNetwork"), new { command = "curl http://x" }), CancellationToken.None);
        time.Advance(PermissionService.DefaultBrokerTimeout);
        var decision = await pending;

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Null(decision.ModifiedInput);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesOutOfCheckAsync()
    {
        var time = new ManualTimeProvider();
        var broker = new NeverDecideBroker();
        var service = new PermissionService(broker, PermissionRuleset.Empty, PermissionMode.Build, timeProvider: time);
        using var cancellationTokenSource = new CancellationTokenSource();

        var pending = service.CheckAsync(Request("Bash", Preset("bashNetwork"), new { command = "curl http://x" }), cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.AsTask());
    }
}
