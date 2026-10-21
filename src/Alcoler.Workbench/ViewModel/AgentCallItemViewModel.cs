using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;

namespace Alcoler.Workbench.ViewModel;

public sealed class AgentCallItemViewModel : TimelineItemViewModel
{
    private ToolCallRow? _tool;
    private SubagentState? _subagent;
    private string? _legacyAgentId;

    public AgentCallItemViewModel(ToolCallRow row) : base(row.RowId)
    {
        Id = row.Id;
        UpdateFrom(row);
    }

    public AgentCallItemViewModel(SystemNoticeRow row) : base(row.RowId) => UpdateFrom(row);

    public ToolCallId? Id { get; }
    public bool IsDispatch => Id is not null;
    public string Title => _subagent?.Description ?? ReadInput("description") ?? "Agent task";
    public string AgentType => _subagent?.AgentType ?? ReadInput("subagent_type") ?? "general-purpose";
    public bool IsExplore => string.Equals(AgentType, "Explore", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => _subagent is { Status: SubagentStatus.Running }
        || _subagent is null && _tool?.Status is ToolCallStatus.Pending or ToolCallStatus.Running;
    public bool HasError => _subagent?.Status == SubagentStatus.Failed
        || _subagent is null && (_tool is { IsError: true } or { Status: ToolCallStatus.Failed });
    public string StatusText => HasError ? "Failed" : string.Empty;
    public string AccessibleText => $"SubAgent {AgentType} · {Title}";
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
        foreach (var block in output?.OfType<ToolResultText>().Reverse() ?? [])
        {
            foreach (var line in block.Text.Split('\n').Reverse())
            {
                if (!line.StartsWith("agentId:", StringComparison.Ordinal))
                    continue;
                var value = line[8..].TrimStart();
                var end = value.IndexOfAny([' ', '\r', '\t', '(']);
                return end < 0 ? value : value[..end];
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
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AgentType));
        OnPropertyChanged(nameof(IsExplore));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AccessibleText));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(Activity));
    }
}
