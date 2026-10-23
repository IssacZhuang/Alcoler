using Alcoler.Contracts.Projections;
using Alcoler.Localization;

namespace Alcoler.ViewModel;

public sealed partial class WorkbenchViewModel
{
    private const string TextDocumentIcon = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h6";
    private const string PlanDocumentIcon = "M9 5H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4M9 3h6v4H9ZM8 12h8M8 16h5";

    public DocumentViewModel OpenTextDocument(string id, string title, string content, string? language = null)
    {
        var workspaceRoot = CurrentWorkspaceRoot;
        var ownerId = CurrentDocumentOwnerId;
        var text = new TextDocumentViewModel(title, content, language);
        var document = OpenDocument(new DocumentViewModel(
            $"text:{id}", "text", title, TextDocumentIcon, text, workspaceRoot, ownerId));
        if (document.Content is TextDocumentViewModel retained)
        {
            retained.UpdateText(content);
        }
        return document;
    }

    public DocumentViewModel OpenPlanDocument(string id, string title, string markdown)
    {
        var workspaceRoot = CurrentWorkspaceRoot;
        var ownerId = CurrentDocumentOwnerId;
        title = string.IsNullOrWhiteSpace(title) ? Loc.Get("workbench_plan") : title.Trim();
        var plan = new PlanDocumentViewModel(title, markdown);
        var document = OpenDocument(new DocumentViewModel(
            $"plan:{id}", "plan", title, PlanDocumentIcon, plan, workspaceRoot, ownerId));
        if (document.Content is PlanDocumentViewModel retained)
        {
            retained.Update(title, markdown);
            document.Title = title;
        }
        return document;
    }

    public void OpenStatusPlan(StatusPlan plan) =>
        OpenPlanDocument(plan.RowId, plan.Title, plan.Markdown);

    public void OpenPendingPlan(PendingInteractionViewModel pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (!pending.HasPlanPreview)
            return;
        var id = pending.ToolCallId is { } toolCallId
            ? toolCallId.Value
            : $"interaction:{pending.InteractionId.Value}";
        OpenPlanDocument(id, pending.PlanTitle, pending.PlanMarkdown!);
    }

    public void OpenToolPlan(ToolCallItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasPlan)
            return;
        OpenPlanDocument(item.RowId, item.PlanTitle, item.PlanMarkdown);
    }

    public async Task OpenStatusActivityAsync(StatusActivity activity)
    {
        try
        {
            if (activity.ChildSessionId is { } childSessionId)
            {
                EnsureDocumentContext();
                var id = $"agent:{childSessionId.Value}";
                var ownerId = CurrentDocumentOwnerId;
                var existing = Documents.VisibleItems.FirstOrDefault(document => document.Id == id && document.OwnerId == ownerId);
                if (existing is not null)
                {
                    Documents.Activate(existing);
                    return;
                }
                var parentSessionId = Active?.SessionId;
                IConversationProjection? Observe() => parentSessionId is { } parent && _runtime is ISessionActivityRuntime activities
                    ? activities.TryObserveSubagent(parent, childSessionId) ?? _runtime.TryObserve(childSessionId)
                    : _runtime.TryObserve(childSessionId);
                var projection = Observe();
                if (projection is null)
                {
                    OpenTextDocument($"agent:{activity.Id}", activity.Title,
                        activity.OutputText ?? Loc.Get("workbench_agent_transcript_unavailable"), "markdown");
                    return;
                }
                var transcript = new SubagentDocumentViewModel(
                    _runtime, childSessionId, CurrentWorkspaceRoot, projection,
                    () => Observe() ?? throw new InvalidOperationException(Loc.Get("workbench_agent_transcript_unavailable")), _scheduler);
                OpenDocument(new DocumentViewModel(
                    id, "subagent", activity.Title, TextDocumentIcon, transcript, CurrentWorkspaceRoot, ownerId));
            }
            else if (activity.OutputPath is { Length: > 0 } path)
            {
                await OpenFileDocumentAsync(path);
            }
            else
            {
                OpenTextDocument($"activity:{activity.Id}", activity.Title,
                    string.IsNullOrEmpty(activity.OutputText) ? Loc.Get("workbench_no_command_output") : activity.OutputText);
            }
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("workbench_activity_error", exception.Message);
        }
    }

    public async Task<DocumentViewModel> OpenFileDocumentAsync(
        string path, string? workspaceRoot = null, string? ownerId = null)
    {
        workspaceRoot ??= CurrentWorkspaceRoot;
        ownerId ??= CurrentDocumentOwnerId;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workspaceRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var invalid = new TextDocumentViewModel(path, string.Empty)
            {
                Error = Loc.Get("workbench_file_path_error", exception.Message),
            };
            return OpenDocument(new DocumentViewModel(
                $"file:{path}", "file", Loc.Get("workbench_invalid_file_path"), TextDocumentIcon, invalid, workspaceRoot, ownerId));
        }
        var canonicalId = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var preview = TextDocumentViewModel.ForFile(fullPath, _scheduler);
        var document = OpenDocument(new DocumentViewModel(
            $"file:{canonicalId}", "file", Path.GetFileName(fullPath), TextDocumentIcon, preview, workspaceRoot, ownerId));
        if (document.Content is TextDocumentViewModel retained && !retained.IsDisposed)
        {
            if (ReferenceEquals(retained, preview))
            {
                await retained.ReloadAsync();
            }
            else
            {
                await retained.LoadingTask;
            }
        }
        return document;
    }
}
