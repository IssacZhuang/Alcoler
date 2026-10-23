using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Localization;

namespace Alcoler.ViewModel;

public sealed class AgentCallItemViewModel : TimelineItemViewModel
{
    private ToolCallRow? _tool;
    private SubagentState? _subagent;
    private string? _legacyAgentId;
    private bool _notified;
    private string? _notifiedTitle;
    private string? _notifiedAgentType;
    private bool _notifiedIsExplore;
    private bool _notifiedIsRunning;
    private bool _notifiedHasError;
    private string? _notifiedStatusText;
    private string? _notifiedAccessibleText;
    private string? _notifiedToolTipText;
    private StatusActivity? _notifiedActivity;

    public AgentCallItemViewModel(ToolCallRow row) : base(row.RowId)
    {
        Id = row.Id;
        UpdateFrom(row);
    }

    public AgentCallItemViewModel(SystemNoticeRow row) : base(row.RowId) => UpdateFrom(row);

    public ToolCallId? Id { get; }
    public bool IsDispatch => Id is not null;
    public string Title => _subagent?.Description ?? ReadInput("description") ?? Loc.Get("agentcall_agent_task");
    public string AgentType => _subagent?.AgentType ?? ReadInput("subagent_type") ?? "general-purpose";
    public bool IsExplore => string.Equals(AgentType, "Explore", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => _subagent is { Status: SubagentStatus.Running }
        || _subagent is null && _tool?.Status is ToolCallStatus.Pending or ToolCallStatus.Running;
    public bool HasError => _subagent?.Status == SubagentStatus.Failed
        || _subagent is null && (_tool is { IsError: true } or { Status: ToolCallStatus.Failed });
    public string StatusText => HasError ? Loc.Get("status_failed") : string.Empty;
    public string AccessibleText => Loc.Get("agentcall_accessible", AgentType, Title);
    public string ToolTipText => HasError
        ? _subagent?.ResultSummary ?? string.Join("\n", _tool?.Output?.OfType<ToolResultText>().Select(block => block.Text) ?? [])
        : AccessibleText;

    public StatusActivity? Activity => _subagent is not { } agent ? null : new StatusActivity
    {
        Id = agent.Id.Value,
        Title = Title,
        RowId = RowId,
        ChildSessionId = agent.ChildSessionId,
        StartedAt = agent.StartedAt,
        StatusText = agent.Status.ToString(),
        OutputText = agent.ResultSummary,
        CanStop = agent.Status == SubagentStatus.Running,
    };

    public void UpdateFrom(ToolCallRow row)
    {
        if (!ReferenceEquals(_tool?.Output, row.Output))
            _legacyAgentId = ReadLegacyAgentId(row.Output);
        _tool = row;
        NotifyChanged();
    }

    public void UpdateFrom(SystemNoticeRow row) => SetSubagent(row.Subagent);

    public void SetSubagent(SubagentState? subagent)
    {
        if (_subagent == subagent)
            return;
        _subagent = subagent;
        NotifyChanged();
    }

    internal string? LegacyAgentId => _legacyAgentId;

    private static string? ReadLegacyAgentId(IReadOnlyList<ToolResultBlock>? output)
    {
        if (output is null)
            return null;
        for (var blockIndex = output.Count - 1; blockIndex >= 0; blockIndex--)
        {
            if (output[blockIndex] is not ToolResultText block)
                continue;
            var text = block.Text;
            var lineEnd = text.Length;
            while (lineEnd >= 0)
            {
                var lineStart = lineEnd == 0 ? 0 : text.LastIndexOf('\n', lineEnd - 1) + 1;
                var line = text.AsSpan(lineStart, lineEnd - lineStart);
                if (line.StartsWith("agentId:", StringComparison.Ordinal))
                {
                    var value = line[8..].TrimStart();
                    var end = -1;
                    for (var index = 0; index < value.Length; index++)
                    {
                        if (value[index] is ' ' or '\r' or '\t' or '(')
                        {
                            end = index;
                            break;
                        }
                    }
                    return end < 0 ? value.ToString() : value[..end].ToString();
                }
                if (lineStart == 0)
                    break;
                lineEnd = lineStart - 1;
            }
        }
        return null;
    }

    internal static bool IsAgentTool(string toolName) => toolName.Equals("Agent", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("Task", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("subagent", StringComparison.OrdinalIgnoreCase);

    private string? ReadInput(string name) => _tool?.Input is { ValueKind: JsonValueKind.Object } input
        && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private void NotifyChanged()
    {
        var title = Title;
        if (!_notified || !string.Equals(title, _notifiedTitle, StringComparison.Ordinal))
        {
            _notifiedTitle = title;
            OnPropertyChanged(nameof(Title));
        }
        var agentType = AgentType;
        if (!_notified || !string.Equals(agentType, _notifiedAgentType, StringComparison.Ordinal))
        {
            _notifiedAgentType = agentType;
            OnPropertyChanged(nameof(AgentType));
        }
        var isExplore = IsExplore;
        if (!_notified || isExplore != _notifiedIsExplore)
        {
            _notifiedIsExplore = isExplore;
            OnPropertyChanged(nameof(IsExplore));
        }
        var isRunning = IsRunning;
        if (!_notified || isRunning != _notifiedIsRunning)
        {
            _notifiedIsRunning = isRunning;
            OnPropertyChanged(nameof(IsRunning));
        }
        var hasError = HasError;
        if (!_notified || hasError != _notifiedHasError)
        {
            _notifiedHasError = hasError;
            OnPropertyChanged(nameof(HasError));
        }
        var statusText = StatusText;
        if (!_notified || !string.Equals(statusText, _notifiedStatusText, StringComparison.Ordinal))
        {
            _notifiedStatusText = statusText;
            OnPropertyChanged(nameof(StatusText));
        }
        var accessibleText = AccessibleText;
        if (!_notified || !string.Equals(accessibleText, _notifiedAccessibleText, StringComparison.Ordinal))
        {
            _notifiedAccessibleText = accessibleText;
            OnPropertyChanged(nameof(AccessibleText));
        }
        var toolTipText = ToolTipText;
        if (!_notified || !string.Equals(toolTipText, _notifiedToolTipText, StringComparison.Ordinal))
        {
            _notifiedToolTipText = toolTipText;
            OnPropertyChanged(nameof(ToolTipText));
        }
        var activity = Activity;
        if (!_notified || !Equals(activity, _notifiedActivity))
        {
            _notifiedActivity = activity;
            OnPropertyChanged(nameof(Activity));
        }
        _notified = true;
    }
}
