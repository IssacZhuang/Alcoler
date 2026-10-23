using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.ViewModel;

public abstract partial class TimelineItemViewModel : ObservableObject
{
    protected TimelineItemViewModel(string rowId) => RowId = rowId;

    public string RowId { get; }
}

public sealed partial class UserMessageItemViewModel : TimelineItemViewModel
{
    public UserMessageItemViewModel(string rowId, string text, IReadOnlyList<FilePart>? images = null,
        MessageId id = default, bool canRewind = false) : base(rowId)
    {
        Id = id;
        Text = text;
        Images = images ?? [];
        CanRewind = canRewind;
    }

    public MessageId Id { get; }

    public string Text { get; }

    public bool CanRewind { get; }

    public IReadOnlyList<FilePart> Images { get; }

    public bool HasImages => Images.Count > 0;
}

public sealed record AssistantMarkdownChunk(string Text);

public sealed partial class AssistantMessageItemViewModel : TimelineItemViewModel
{
    private int _chunkStart;
    private int _scanOffset;
    private int _candidateBoundary = -1;
    private bool _inFence;
    private char _fenceChar;
    private int _fenceLength;

    public AssistantMessageItemViewModel(string rowId, MessageId id) : base(rowId) => Id = id;

    public MessageId Id { get; }

    [ObservableProperty]
    private string _markdownText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _reasoningBlocks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStreamingTail), nameof(HasFinalMarkdown))]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isReasoningExpanded;

    [ObservableProperty]
    private bool _canFork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStreamingTail))]
    private string _streamingTail = string.Empty;

    [ObservableProperty]
    private bool _streamingTailIsCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFinalMarkdown))]
    private string _finalMarkdown = string.Empty;

    public ObservableCollection<AssistantMarkdownChunk> StreamingChunks { get; } = [];

    public bool HasStreamingTail => IsStreaming && StreamingTail.Length > 0;

    public bool HasFinalMarkdown => !IsStreaming && FinalMarkdown.Length > 0;

    public void UpdateFrom(AssistantMessageRow row)
    {
        var textBuilder = new StringBuilder();
        var reasoning = new List<string>();
        var firstText = true;
        foreach (var part in row.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    if (!firstText)
                        textBuilder.Append("\n\n");
                    textBuilder.Append(textPart.Text);
                    firstText = false;
                    break;
                case ReasoningPart reasoningPart:
                    reasoning.Add(reasoningPart.Text);
                    break;
            }
        }
        var text = textBuilder.ToString();
        var textChanged = !string.Equals(MarkdownText, text, StringComparison.Ordinal);
        if (textChanged)
        {
            if (row.IsStreaming)
            {
                if (!IsStreaming || !text.StartsWith(MarkdownText, StringComparison.Ordinal))
                    ResetStreamingState();
                ScanStreaming(text);
            }
            MarkdownText = text;
        }

        if (row.IsStreaming)
        {
            if (FinalMarkdown.Length > 0)
                FinalMarkdown = string.Empty;
        }
        else
        {
            if (!string.Equals(FinalMarkdown, text, StringComparison.Ordinal))
                FinalMarkdown = text;
            if (StreamingChunks.Count > 0)
                StreamingChunks.Clear();
            if (StreamingTail.Length > 0)
                StreamingTail = string.Empty;
            if (StreamingTailIsCode)
                StreamingTailIsCode = false;
        }

        var reasoningUnchanged = ReasoningBlocks.Count == reasoning.Count;
        if (reasoningUnchanged)
        {
            for (var index = 0; index < reasoning.Count; index++)
            {
                if (!string.Equals(ReasoningBlocks[index], reasoning[index], StringComparison.Ordinal))
                {
                    reasoningUnchanged = false;
                    break;
                }
            }
        }
        if (!reasoningUnchanged)
            ReasoningBlocks = reasoning.ToArray();

        IsStreaming = row.IsStreaming;
        CanFork = row.CanFork;
    }

    private void ResetStreamingState()
    {
        if (StreamingChunks.Count > 0)
            StreamingChunks.Clear();
        if (StreamingTail.Length > 0)
            StreamingTail = string.Empty;
        if (StreamingTailIsCode)
            StreamingTailIsCode = false;
        _chunkStart = 0;
        _scanOffset = 0;
        _candidateBoundary = -1;
        _inFence = false;
        _fenceChar = '\0';
        _fenceLength = 0;
    }

    private void ScanStreaming(string text)
    {
        while (_scanOffset < text.Length)
        {
            var newline = text.IndexOf('\n', _scanOffset);
            if (newline < 0)
                break;
            var contentEnd = newline;
            if (contentEnd > _scanOffset && text[contentEnd - 1] == '\r')
                contentEnd--;
            ProcessLine(text.AsSpan(_scanOffset, contentEnd - _scanOffset), newline + 1, text);
            _scanOffset = newline + 1;
        }

        var tailIsCode = _inFence;
        if (_scanOffset < text.Length)
        {
            var partial = text.AsSpan(_scanOffset);
            if (partial.Length > 0 && partial[^1] == '\r')
                partial = partial[..^1];
            if (IsBlank(partial))
            {
                _candidateBoundary = text.Length;
            }
            else
            {
                ConfirmCandidate(partial, text);
                if (!_inFence && TryFence(partial, out _, out _, out _))
                    tailIsCode = true;
            }
        }

        StreamingTail = _chunkStart >= text.Length ? string.Empty : text[_chunkStart..];
        StreamingTailIsCode = tailIsCode;
    }

    private void ProcessLine(ReadOnlySpan<char> line, int lineEnd, string text)
    {
        if (_inFence)
        {
            if (!IsClosingFence(line))
                return;
            _inFence = false;
            CommitThrough(lineEnd, text);
            _candidateBoundary = -1;
            return;
        }

        if (TryFence(line, out var delimiter, out var length, out _))
        {
            if (_candidateBoundary >= 0)
                CommitThrough(_candidateBoundary, text);
            _candidateBoundary = -1;
            _inFence = true;
            _fenceChar = delimiter;
            _fenceLength = length;
            return;
        }

        if (IsBlank(line))
        {
            _candidateBoundary = lineEnd;
            return;
        }

        ConfirmCandidate(line, text);
    }

    private void ConfirmCandidate(ReadOnlySpan<char> line, string text)
    {
        if (_candidateBoundary < 0)
            return;
        if (!StartsContinuation(line))
            CommitThrough(_candidateBoundary, text);
        _candidateBoundary = -1;
    }

    private void CommitThrough(int boundary, string text)
    {
        boundary = Math.Min(boundary, text.Length);
        if (boundary > _chunkStart)
        {
            var chunk = text[_chunkStart..boundary];
            if (!string.IsNullOrWhiteSpace(chunk))
                StreamingChunks.Add(new AssistantMarkdownChunk(chunk));
            _chunkStart = boundary;
        }
        _candidateBoundary = -1;
    }

    private bool IsClosingFence(ReadOnlySpan<char> line)
    {
        if (!TryFence(line, out var delimiter, out var length, out var restStart))
            return false;
        return delimiter == _fenceChar && length >= _fenceLength && line[restStart..].Trim().IsEmpty;
    }

    private static bool TryFence(ReadOnlySpan<char> line, out char delimiter, out int length, out int restStart)
    {
        delimiter = '\0';
        length = 0;
        restStart = 0;
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
            indent++;
        if (indent > 3 || indent >= line.Length)
            return false;
        var candidate = line[indent];
        if (candidate is not ('`' or '~'))
            return false;
        var run = 0;
        while (indent + run < line.Length && line[indent + run] == candidate)
            run++;
        if (run < 3)
            return false;
        delimiter = candidate;
        length = run;
        restStart = indent + run;
        return true;
    }

    private static bool IsBlank(ReadOnlySpan<char> line) => line.Trim().IsEmpty;

    private static bool StartsContinuation(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
            return false;
        var first = line[0];
        if (first is ' ' or '\t' or '>' or '|' or '<')
            return true;
        if ((first is '-' or '*' or '+') && line.Length > 1 && (line[1] is ' ' or '\t'))
            return true;
        var index = 0;
        while (index < line.Length && char.IsAsciiDigit(line[index]))
            index++;
        return index > 0 && index + 1 < line.Length &&
               (line[index] is '.' or ')') && (line[index + 1] is ' ' or '\t');
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

    public string DisplayStatusText => HasError ? Loc.Get("status_error") : Status switch
    {
        ToolCallStatus.Pending => Loc.Get("status_pending"),
        ToolCallStatus.Running => Loc.Get("status_running"),
        ToolCallStatus.AwaitingPermission => Loc.Get("status_needs_permission"),
        ToolCallStatus.Cancelled => Loc.Get("status_cancelled"),
        _ => string.Empty,
    };

    public string AccessibleDurationText => string.IsNullOrEmpty(DurationText)
        ? string.Empty
        : Loc.Get("timeline_elapsed_time", DurationText);

    public bool HasImages => ImageMedia.Count > 0;

    public bool HasFileChange => FileChange is { Added: > 0 } or { Removed: > 0 };

    public bool HasAddedLines => FileChange is { Added: > 0 };

    public bool HasRemovedLines => FileChange is { Removed: > 0 };

    public string AddedLinesText => $"+{FileChange?.Added ?? 0}";

    public string RemovedLinesText => $"-{FileChange?.Removed ?? 0}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileChange), nameof(HasAddedLines), nameof(HasRemovedLines),
        nameof(AddedLinesText), nameof(RemovedLinesText))]
    private FileChangeStats? _fileChange;

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
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    private string _planMarkdown = string.Empty;

    [ObservableProperty]
    private string _planTitle = string.Empty;

    public bool HasPlan => PlanMarkdown.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImages))]
    private IReadOnlyList<ToolResultMedia> _imageMedia = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowStatus), nameof(DisplayStatusText))]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleDurationText))]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = JsonElementContext.Default,
    };

    private static readonly string[] SummaryKeys =
        ["description", "file_path", "path", "command", "pattern", "query", "url"];

    private JsonElement _inputSource;
    private bool _hasInputSource;
    private IReadOnlyList<ToolResultBlock>? _outputSource;
    private bool _hasOutputSource;

    public void UpdateFrom(ToolCallRow row)
    {
        if (!_hasInputSource || !JsonElement.DeepEquals(_inputSource, row.Input))
        {
            _hasInputSource = true;
            _inputSource = row.Input.Clone();
            InputJson = JsonSerializer.Serialize(row.Input, IndentedJson);
            var plan = string.Equals(ToolName, "ExitPlanMode", StringComparison.OrdinalIgnoreCase)
                ? ReadText(row.Input, "plan") ?? string.Empty
                : string.Empty;
            PlanMarkdown = plan;
            PlanTitle = plan.Length == 0
                ? string.Empty
                : PlanDocumentViewModel.ResolveTitle(ReadText(row.Input, "summary"), plan);
        }

        var statusChanged = row.Status != Status;
        Status = row.Status;
        StatusText = StatusKey(row.Status);
        Summary = GetSummary(row.Input);
        FileChange = row.FileChange;
        IsError = row.IsError;
        DurationText = row.Duration is { } duration
            ? duration.TotalMilliseconds < 1000
                ? $"{duration.TotalMilliseconds:F0}ms"
                : $"{duration.TotalSeconds:F1}s"
            : string.Empty;

        if (statusChanged || !_hasOutputSource || !ReferenceEquals(_outputSource, row.Output)
            || _outputSource?.Count != row.Output?.Count)
        {
            _hasOutputSource = true;
            _outputSource = row.Output;
            OutputText = row.Output is null
                ? string.Empty
                : string.Join("\n", row.Output.OfType<ToolResultText>().Select(b => b.Text));
            ToolResultMedia[] images = row.Output is null
                ? []
                : [.. row.Output.OfType<ToolResultMedia>().Where(media => media.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))];
            if (!ImageMedia.SequenceEqual(images))
                ImageMedia = images;
        }
    }

    private static string StatusKey(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Pending => "pending",
        ToolCallStatus.Running => "running",
        ToolCallStatus.AwaitingPermission => "awaitingpermission",
        ToolCallStatus.Succeeded => "succeeded",
        ToolCallStatus.Failed => "failed",
        ToolCallStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static (string DisplayName, string IconPath) GetPresentation(string toolName) => toolName.ToLowerInvariant() switch
    {
        "bash" or "shell" or "terminal" or "runcommand" => (Loc.Get("timeline_tool_terminal"), ToolIcons.Terminal),
        "read" or "readfile" => (Loc.Get("timeline_tool_read"), ToolIcons.Read),
        "edit" or "editfile" => (Loc.Get("timeline_tool_edit"), ToolIcons.Edit),
        "write" or "writefile" => (Loc.Get("timeline_tool_write"), ToolIcons.Write),
        "grep" or "glob" => (Loc.Get("timeline_tool_search"), ToolIcons.Search),
        "browser" => (Loc.Get("timeline_tool_browser"), ToolIcons.Browser),
        "todoread" or "todowrite" => (Loc.Get("timeline_tool_tasks"), ToolIcons.Tasks),
        "task" => (Loc.Get("timeline_tool_agent"), ToolIcons.Agent),
        "exitplanmode" => (Loc.Get("timeline_tool_plan"), ToolIcons.Plan),
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

    private static string? ReadText(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetSummary(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        foreach (var key in SummaryKeys)
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

public sealed class ChangedFileItemViewModel
{
    public ChangedFileItemViewModel(FileChangeSummaryItem file)
    {
        Path = file.Path;
        HasAddedLines = file.Added > 0;
        HasRemovedLines = file.Removed > 0;
        AddedLinesText = $"+{file.Added}";
        RemovedLinesText = $"-{file.Removed}";
    }

    public string Path { get; }

    public string FileName => SplitPath(Path).FileName;

    public string DirectoryPath => SplitPath(Path).DirectoryPath;

    public bool HasAddedLines { get; }

    public bool HasRemovedLines { get; }

    public string AddedLinesText { get; }

    public string RemovedLinesText { get; }

    private static (string FileName, string DirectoryPath) SplitPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0
            ? (normalized, string.Empty)
            : (normalized[(index + 1)..], normalized[..(index + 1)]);
    }
}

public sealed partial class FileChangeSummaryItemViewModel : TimelineItemViewModel
{
    public FileChangeSummaryItemViewModel(string rowId, FileChangeSummaryRow row) : base(rowId) =>
        UpdateFrom(row);

    private IReadOnlyList<ChangedFileItemViewModel> _files = [];
    private int _added;
    private int _removed;

    public IReadOnlyList<ChangedFileItemViewModel> Files => _files;

    public int Added => _added;

    public int Removed => _removed;

    public bool HasAddedLines => _added > 0;

    public bool HasRemovedLines => _removed > 0;

    public string FilesChangedText => _files.Count == 1
        ? Loc.Get("timeline_files_changed_one")
        : Loc.Get("timeline_files_changed_many", _files.Count);

    public string AddedLinesText => $"+{_added}";

    public string RemovedLinesText => $"-{_removed}";

    [ObservableProperty]
    private bool _isExpanded;

    public void UpdateFrom(FileChangeSummaryRow row)
    {
        _files = row.Files.Select(file => new ChangedFileItemViewModel(file)).ToArray();
        _added = row.Added;
        _removed = row.Removed;
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(Added));
        OnPropertyChanged(nameof(Removed));
        OnPropertyChanged(nameof(HasAddedLines));
        OnPropertyChanged(nameof(HasRemovedLines));
        OnPropertyChanged(nameof(FilesChangedText));
        OnPropertyChanged(nameof(AddedLinesText));
        OnPropertyChanged(nameof(RemovedLinesText));
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
        "turn_error" => Loc.Get("status_error"),
        "compact" => Loc.Get("status_context"),
        "subagent" => Loc.Get("timeline_tool_agent"),
        _ => string.Join(" ", Kind.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    public bool IsError => Kind.Contains("error", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("failed", StringComparison.OrdinalIgnoreCase);

    public string Text { get; }
}

public static class TimelineItemFactory
{
    public static TimelineItemViewModel? Create(ConversationRow row) => row switch
    {
        UserMessageRow user => new UserMessageItemViewModel(
            user.RowId, user.Text, user.Images, new MessageId(user.RowId), user.CanRewind),
        AssistantMessageRow assistant => CreateAssistant(assistant),
        ToolCallRow tool when AgentCallItemViewModel.IsAgentTool(tool.ToolName) => new AgentCallItemViewModel(tool),
        ToolCallRow tool => CreateTool(tool),
        FileChangeSummaryRow summary => new FileChangeSummaryItemViewModel(summary.RowId, summary),
        SystemNoticeRow { Subagent: not null } agent => new AgentCallItemViewModel(agent),
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
            case (FileChangeSummaryItemViewModel summary, FileChangeSummaryRow summaryRow):
                summary.UpdateFrom(summaryRow);
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
