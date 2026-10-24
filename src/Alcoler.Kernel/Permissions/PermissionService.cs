using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Permissions;

public sealed class PermissionService : IPermissionGate
{
    public const string PlanModeDenyReason = "Plan mode only allows read-only, non-destructive tools";
    public const string BrokerTimeoutReason = "permission request timed out";
    private const string ExitPlanModeToolName = "ExitPlanMode";
    public static readonly TimeSpan DefaultBrokerTimeout = TimeSpan.FromMinutes(10);

    private readonly IPermissionBroker _broker;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _brokerTimeout;
    private readonly ILogger? _logger;
    private readonly IReadOnlyList<string> _disallowedTools;
    private readonly object _sync = new();
    private PermissionRule[] _sessionRulesSnapshot = [];

    private PermissionRuleset _projectRules;
    private PermissionMode _mode;
    private bool _planEnabled;

    public PermissionService(
        IPermissionBroker broker,
        PermissionRuleset projectRules,
        PermissionMode initialMode,
        IReadOnlyList<string>? disallowedTools = null,
        TimeProvider? timeProvider = null,
        TimeSpan? brokerTimeout = null,
        ILogger? logger = null,
        bool planEnabled = false)
    {
        _broker = broker;
        _projectRules = projectRules;
        _mode = initialMode;
        _planEnabled = planEnabled;
        _disallowedTools = disallowedTools ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _brokerTimeout = brokerTimeout ?? DefaultBrokerTimeout;
        _logger = logger;
    }

    public PermissionMode Mode => _mode;

    public bool PlanEnabled => _planEnabled;

    public ExecutionState ExecutionState => new(_mode, _planEnabled);

    public void SetExecutionState(PermissionMode mode, bool planEnabled)
    {
        _mode = mode;
        _planEnabled = planEnabled;
    }

    public void SetMode(PermissionMode mode) => _mode = mode;

    public void SetPlanEnabled(bool planEnabled) => _planEnabled = planEnabled;

    public void GrantSessionPermission(PermissionRule rule)
    {
        lock (_sync)
        {
            // Readers never lock; publication is a swap onto a fully built array.
            var current = _sessionRulesSnapshot;
            var next = new PermissionRule[current.Length + 1];
            current.AsSpan().CopyTo(next);
            next[^1] = rule;
            Volatile.Write(ref _sessionRulesSnapshot, next);
        }
    }

    public void UpdateProjectRules(PermissionRuleset ruleset) => _projectRules = ruleset;

    public async ValueTask<PermissionDecision> CheckAsync(PermissionCheckRequest request, CancellationToken cancellationToken)
    {
        var tool = request.Tool;
        var planEnabled = request.PlanEnabled || _planEnabled;

        // ExitPlanMode must never reach the broker outside plan mode.
        if (string.Equals(request.ToolName, ExitPlanModeToolName, StringComparison.OrdinalIgnoreCase)
            && !planEnabled)
            return PermissionDecision.Deny;

        // Interactive tools only need the broker when they request approval; tools such as
        // AskUserQuestion own their prompt and must not be gated by a second permission card.
        if (tool.RequiresUserInteraction && tool.NeedsApproval)
            return await AskBrokerAsync(request, "tool requires user interaction", cancellationToken).ConfigureAwait(false);

        // The always-ask gate runs before the yolo bypass and survives it.
        var gate = EvaluateAlwaysAskGate(request);
        if (gate.Verdict == PermissionVerdict.Deny)
            return PermissionDecision.Deny;

        // Config-disallowed tools must deny even in yolo, so this check precedes the yolo bypass.
        if (IsDisallowed(request.ToolName))
            return PermissionDecision.Deny;

        // Plan mode overrides project allow and ask rules so they cannot escalate non-plan-safe tools.
        if (planEnabled)
            return DecidePlanMode(request);

        if (gate.Verdict == PermissionVerdict.Ask)
            return await AskBrokerAsync(request, gate.Reason!, cancellationToken).ConfigureAwait(false);

        if (gate.Verdict == PermissionVerdict.Allow)
            return PermissionDecision.Allow;

        if (_mode == PermissionMode.Yolo && !planEnabled)
            return PermissionDecision.Allow;

        return await DecideByModeDefaultsAsync(request, tool, cancellationToken).ConfigureAwait(false);
    }

    private (PermissionVerdict? Verdict, string? Reason) EvaluateAlwaysAskGate(PermissionCheckRequest request)
    {
        var sessionRules = Volatile.Read(ref _sessionRulesSnapshot);
        var projectRules = _projectRules;

        if (sessionRules.Length > 0)
        {
            foreach (var rule in sessionRules)
                if (rule.Verdict == PermissionVerdict.Ask && Matches(rule, request))
                    return (PermissionVerdict.Ask, $"matched session ask rule {DescribeRule(rule)}");
        }

        if (FirstMatch(projectRules.Deny, request) is not null)
            return (PermissionVerdict.Deny, null);

        if (sessionRules.Length > 0)
        {
            foreach (var rule in sessionRules)
                if (rule.Verdict == PermissionVerdict.Deny && Matches(rule, request))
                    return (PermissionVerdict.Deny, null);

            // Session allow grants must precede project ask rules, or "don't ask again" approvals would re-ask forever.
            foreach (var rule in sessionRules)
                if (rule.Verdict == PermissionVerdict.Allow && Matches(rule, request))
                    return (PermissionVerdict.Allow, null);
        }

        if (FirstMatch(projectRules.Ask, request) is { } askRule)
            return (PermissionVerdict.Ask, $"matched project ask rule {DescribeRule(askRule)}");

        if (FirstMatch(projectRules.Allow, request) is not null)
            return (PermissionVerdict.Allow, null);

        return (null, null);
    }

    private static PermissionDecision DecidePlanMode(PermissionCheckRequest request)
    {
        var tool = request.Tool;
        if (tool.Destructive)
            return PermissionDecision.Deny;
        if (tool.ReadOnly || request.TreatAsReadOnly)
            return PermissionDecision.Allow;
        if (tool.AllowedInPlanMode)
            return PermissionDecision.Allow;
        if (tool.SideEffectScope == SideEffectScope.None)
            return PermissionDecision.Allow;
        return PermissionDecision.Deny;
    }

    private async ValueTask<PermissionDecision> DecideByModeDefaultsAsync(
        PermissionCheckRequest request,
        ToolMetadata tool,
        CancellationToken cancellationToken)
    {
        if (request.TreatAsReadOnly)
            return PermissionDecision.Allow;

        if (tool.ReadOnly && tool.RiskLevel == RiskLevel.Low && tool.SideEffectScope != SideEffectScope.UserInteraction)
            return PermissionDecision.Allow;

        if (tool.RequiresUserInteraction && !tool.NeedsApproval
            && tool.SideEffectScope == SideEffectScope.UserInteraction)
            return PermissionDecision.Allow;

        if (tool.NeedsApproval)
            return await AskBrokerAsync(request, "tool metadata requires approval", cancellationToken).ConfigureAwait(false);

        if (tool.Destructive)
            return await AskBrokerAsync(request, "tool is destructive", cancellationToken).ConfigureAwait(false);

        if (tool.RiskLevel >= RiskLevel.High)
            return await AskBrokerAsync(request, $"tool risk level {tool.RiskLevel} requires approval", cancellationToken).ConfigureAwait(false);

        if (tool.SideEffectScope == SideEffectScope.Workspace)
        {
            if (_mode == PermissionMode.Edit)
                return PermissionDecision.Allow;
            return await AskBrokerAsync(request, "workspace-modifying tool requires approval in build mode", cancellationToken).ConfigureAwait(false);
        }

        return await AskBrokerAsync(request, $"no rule or mode default allows this tool in {_mode} mode", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PermissionDecision> AskBrokerAsync(
        PermissionCheckRequest request,
        string reason,
        CancellationToken cancellationToken)
    {
        var permissionRequest = new PermissionRequest(
            StrongId.NewInteractionId(),
            request.SessionId,
            request.ToolName,
            request.Input,
            request.Tool.RiskLevel,
            reason,
            AllowsFeedback: string.Equals(request.ToolName, ExitPlanModeToolName, StringComparison.OrdinalIgnoreCase),
            ToolCallId: request.ToolCallId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            linked,
            _brokerTimeout,
            Timeout.InfiniteTimeSpan);
        try
        {
            return await _broker.DecideAsync(permissionRequest, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "Permission request for {ToolName} timed out after {Timeout}: {Reason}",
                request.ToolName, _brokerTimeout, BrokerTimeoutReason);
            return PermissionDecision.Deny;
        }
    }

    private bool IsDisallowed(string toolName)
    {
        foreach (var disallowed in _disallowedTools)
            if (string.Equals(disallowed, toolName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool Matches(PermissionRule rule, PermissionCheckRequest request) =>
        PermissionRuleMatcher.Matches(rule, request.ToolName, request.Input);

    private static PermissionRule? FirstMatch(IReadOnlyList<PermissionRule> rules, PermissionCheckRequest request)
    {
        foreach (var rule in rules)
            if (Matches(rule, request))
                return rule;
        return null;
    }

    private static string DescribeRule(PermissionRule rule) =>
        rule.Content is null ? rule.ToolName : $"{rule.ToolName}({rule.Content})";
}
