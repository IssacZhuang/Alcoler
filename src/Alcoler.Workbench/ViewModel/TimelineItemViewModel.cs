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
        (DisplayName, IconPath) = GetPresentation(toolName);
    }

    public ToolCallId Id { get; }

    public string ToolName { get; }

    public string DisplayName { get; }

    public string IconPath { get; }

    public bool HasError => IsError || Status == ToolCallStatus.Failed;

    public bool ShowStatus => HasError || Status != ToolCallStatus.Succeeded;

    public string DisplayStatusText => HasError ? "Error" : Status switch
    {
        ToolCallStatus.Pending => "Pending",
        ToolCallStatus.Running => "Running",
        ToolCallStatus.AwaitingPermission => "Needs permission",
        ToolCallStatus.Cancelled => "Cancelled",
        _ => string.Empty,
    };

    public string AccessibleDurationText => string.IsNullOrEmpty(DurationText) ? string.Empty : $"Elapsed time {DurationText}";

    [ObservableProperty]
    private string _inputJson = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayStatusText))]
    private ToolCallStatus _status;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayStatusText))]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleDurationText))]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public void UpdateFrom(ToolCallRow row)
    {
        InputJson = JsonSerializer.Serialize(row.Input, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Status = row.Status;
        StatusText = row.Status.ToString().ToLowerInvariant();
        Summary = GetSummary(row.Input);
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

    private static (string DisplayName, string IconPath) GetPresentation(string toolName) => toolName.ToLowerInvariant() switch
    {
        "bash" or "shell" or "terminal" or "runcommand" => ("Terminal", ToolIcons.Terminal),
        "readfile" => ("Read", ToolIcons.Read),
        "editfile" => ("Edit", ToolIcons.Edit),
        "writefile" => ("Write", ToolIcons.Write),
        "grep" or "glob" => ("Search", ToolIcons.Search),
        "browser" => ("Browser", ToolIcons.Browser),
        "todoread" or "todowrite" => ("Tasks", ToolIcons.Tasks),
        "task" => ("Agent", ToolIcons.Agent),
        "exitplanmode" => ("Plan", ToolIcons.Plan),
        _ => (toolName, ToolIcons.Tool),
    };

    private static class ToolIcons
    {
        internal const string Terminal = "M4 17l6-6-6-6M12 19h8";
        internal const string Read = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h6";
        internal const string Edit = "M12 20h9M16.5 3.5a2.12 2.12 0 0 1 3 3L9 17l-4 1 1-4ZM15 5l3 3";
        internal const string Write = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M12 12v6M9 15h6";
        internal const string Search = "M21 21l-4.34-4.34M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z";
        internal const string Browser = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20ZM2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10Z";
        internal const string Tasks = "m3 6 2 2 4-4M13 6h8m-18 6 2 2 4-4M13 12h8m-18 6 2 2 4-4M13 18h8";
        internal const string Agent = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75";
        internal const string Plan = "M9 5H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4M9 3h6v4H9ZM8 12h8M8 16h5";
        internal const string Tool = "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76Z";
    }

    private static string GetSummary(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        foreach (var key in new[] { "description", "file_path", "path", "command", "pattern", "query", "url" })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                var text = value.GetString()!;
                var firstLine = text.Split(['\r', '\n'], 2)[0];
                return firstLine.Length > 160 ? firstLine[..157] + "..." : firstLine;
            }
        }
        return string.Empty;
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

    public string DisplayKind => Kind.ToLowerInvariant() switch
    {
        "turn_error" => "Error",
        "compact" => "Context",
        "subagent" => "Agent",
        _ => string.Join(" ", Kind.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    public bool IsError => Kind.Contains("error", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("failed", StringComparison.OrdinalIgnoreCase);

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
