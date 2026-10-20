using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Workbench.ViewModel;

public abstract partial class TimelineItemViewModel : ObservableObject
{
    protected TimelineItemViewModel(string rowId) => RowId = rowId;

    public string RowId { get; }
}

public sealed partial class UserMessageItemViewModel : TimelineItemViewModel
{
    public UserMessageItemViewModel(string rowId, string text) : base(rowId) => Text = text;

    public string Text { get; }
}

public sealed partial class AssistantMessageItemViewModel : TimelineItemViewModel
{
    public AssistantMessageItemViewModel(string rowId, MessageId id) : base(rowId) => Id = id;

    public MessageId Id { get; }

    [ObservableProperty]
    private string _markdownText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _reasoningBlocks = [];

    [ObservableProperty]
    private bool _isStreaming;

    public void UpdateFrom(AssistantMessageRow row)
    {
        var text = string.Join("\n\n", row.Parts.OfType<TextPart>().Select(p => p.Text));
        string[] reasoning = [.. row.Parts.OfType<ReasoningPart>().Select(p => p.Text)];
        MarkdownText = text;
        ReasoningBlocks = reasoning;
        IsStreaming = row.IsStreaming;
    }
}

public sealed partial class ToolCallItemViewModel : TimelineItemViewModel
{
    public ToolCallItemViewModel(string rowId, ToolCallId id, string toolName) : base(rowId)
    {
        Id = id;
        ToolName = toolName;
    }

    public ToolCallId Id { get; }

    public string ToolName { get; }

    [ObservableProperty]
    private string _inputJson = string.Empty;

    [ObservableProperty]
    private ToolCallStatus _status;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public void UpdateFrom(ToolCallRow row)
    {
        InputJson = JsonSerializer.Serialize(row.Input, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Status = row.Status;
        StatusText = row.Status.ToString().ToLowerInvariant();
        IsError = row.IsError;
        DurationText = row.Duration is { } duration
            ? duration.TotalMilliseconds < 1000
                ? $"{duration.TotalMilliseconds:F0}ms"
                : $"{duration.TotalSeconds:F1}s"
            : string.Empty;
        OutputText = row.Output is null
            ? string.Empty
            : string.Join("\n", row.Output.OfType<ToolResultText>().Select(b => b.Text));
    }
}

public sealed partial class SystemNoticeItemViewModel : TimelineItemViewModel
{
    public SystemNoticeItemViewModel(string rowId, string kind, string text) : base(rowId)
    {
        Kind = kind;
        Text = text;
    }

    public string Kind { get; }

    public string Text { get; }
}

public static class TimelineItemFactory
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static TimelineItemViewModel? Create(ConversationRow row) => row switch
    {
        UserMessageRow user => new UserMessageItemViewModel(user.RowId, user.Text),
        AssistantMessageRow assistant => CreateAssistant(assistant),
        ToolCallRow tool => CreateTool(tool),
        SystemNoticeRow notice => new SystemNoticeItemViewModel(notice.RowId, notice.Kind, notice.Text),
        _ => null,
    };

    public static bool Update(TimelineItemViewModel item, ConversationRow row)
    {
        switch (item, row)
        {
            case (AssistantMessageItemViewModel assistant, AssistantMessageRow assistantRow):
                assistant.UpdateFrom(assistantRow);
                return true;
            case (ToolCallItemViewModel tool, ToolCallRow toolRow):
                tool.UpdateFrom(toolRow);
                return true;
            default:
                return false;
        }
    }

    private static AssistantMessageItemViewModel CreateAssistant(AssistantMessageRow row)
    {
        var item = new AssistantMessageItemViewModel(row.RowId, row.Id);
        item.UpdateFrom(row);
        return item;
    }

    private static ToolCallItemViewModel CreateTool(ToolCallRow row)
    {
        var item = new ToolCallItemViewModel(row.RowId, row.Id, row.ToolName);
        item.UpdateFrom(row);
        return item;
    }
}
