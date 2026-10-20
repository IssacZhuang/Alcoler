using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Permissions;

public sealed class PermissionService : IPermissionGate
{
    public const string PlanModeDenyReason = "Plan mode only allows read-only, non-destructive tools";
    public const string BrokerTimeoutReason = "permission request timed out";
    public static readonly TimeSpan DefaultBrokerTimeout = TimeSpan.FromMinutes(10);

    private readonly IPermissionBroker _broker;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _brokerTimeout;
    private readonly ILogger? _logger;
    private readonly IReadOnlyList<string> _disallowedTools;
    private readonly object _sync = new();
    private readonly List<PermissionRule> _sessionRules = [];

    private PermissionRuleset _projectRules;
    private PermissionMode _mode;

    public PermissionService(
        IPermissionBroker broker,
        PermissionRuleset projectRules,
        PermissionMode initialMode,
        IReadOnlyList<string>? disallowedTools = null,
        TimeProvider? timeProvider = null,
        TimeSpan? brokerTimeout = null,
        ILogger? logger = null)
    {
        _broker = broker;
        _projectRules = projectRules;
        _mode = initialMode;
        _disallowedTools = disallowedTools ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _brokerTimeout = brokerTimeout ?? DefaultBrokerTimeout;
        _logger = logger;
    }

    public PermissionMode Mode => _mode;

    public void SetMode(PermissionMode mode) => _mode = mode;

    public void GrantSessionPermission(PermissionRule rule)
    {
        lock (_sync) _sessionRules.Add(rule);
    }

    public void UpdateProjectRules(PermissionRuleset ruleset) => _projectRules = ruleset;

    public async ValueTask<PermissionDecision> CheckAsync(PermissionCheckRequest request, CancellationToken cancellationToken)
    {
        var tool = request.Tool;

        if (_mode == PermissionMode.Plan && !tool.AllowedInPlanMode && !request.TreatAsReadOnly)
            return PermissionDecision.Deny;

        if (tool.RequiresUserInteraction)
            return await AskBrokerAsync(request, "tool requires user interaction", cancellationToken).ConfigureAwait(false);

        // The always-ask gate runs before the yolo bypass and survives it.
        var gate = EvaluateAlwaysAskGate(request);
        if (gate.Verdict == PermissionVerdict.Deny)
            return PermissionDecision.Deny;
        if (gate.Verdict == PermissionVerdict.Allow)
            return PermissionDecision.Allow;
        if (gate.Verdict == PermissionVerdict.Ask)
            return await AskBrokerAsync(request, gate.Reason!, cancellationToken).ConfigureAwait(false);

        // Config-disallowed tools must deny even in yolo, so this check precedes the yolo bypass.
        if (IsDisallowed(request.ToolName))
            return PermissionDecision.Deny;

        if (_mode == PermissionMode.Yolo)
            return PermissionDecision.Allow;

        if (_mode == PermissionMode.Plan)
            return DecidePlanReadOnly(tool);

        return await DecideByModeDefaultsAsync(request, tool, cancellationToken).ConfigureAwait(false);
    }

    private (PermissionVerdict? Verdict, string? Reason) EvaluateAlwaysAskGate(PermissionCheckRequest request)
    {
        PermissionRule[] sessionRules;
        PermissionRuleset projectRules;
        lock (_sync)
        {
            sessionRules = [.. _sessionRules];
            projectRules = _projectRules;
        }

        foreach (var rule in sessionRules)
            if (rule.Verdict == PermissionVerdict.Ask && Matches(rule, request))
                return (PermissionVerdict.Ask, $"matched session ask rule {DescribeRule(rule)}");

        if (FirstMatch(projectRules.Deny, request) is not null)
            return (PermissionVerdict.Deny, null);

        foreach (var rule in sessionRules)
            if (rule.Verdict == PermissionVerdict.Deny && Matches(rule, request))
                return (PermissionVerdict.Deny, null);

        // Session allow grants must precede project ask rules, or "don't ask again" approvals would re-ask forever.
        foreach (var rule in sessionRules)
            if (rule.Verdict == PermissionVerdict.Allow && Matches(rule, request))
                return (PermissionVerdict.Allow, null);

        if (FirstMatch(projectRules.Ask, request) is { } askRule)
            return (PermissionVerdict.Ask, $"matched project ask rule {DescribeRule(askRule)}");

        if (FirstMatch(projectRules.Allow, request) is not null)
            return (PermissionVerdict.Allow, null);

        return (null, null);
    }

    private static PermissionDecision DecidePlanReadOnly(ToolMetadata tool)
    {
        // AllowedInPlanMode is guaranteed by the plan transition check in CheckAsync.
        if (tool.ReadOnly)
            return PermissionDecision.Allow;
        if (tool.SideEffectScope == SideEffectScope.None && !tool.Destructive)
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
            reason);

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
