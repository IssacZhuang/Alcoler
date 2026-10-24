using System.Collections.ObjectModel;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Kernel.Workflows;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class ComposerViewModel : ObservableObject
{
    private static readonly ComposerPromptHistory SharedHistory = new();
    private bool _restoringMode;
    private readonly ComposerPromptHistory _history;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InputImageBlock, ComposerImageAttachment> _imageSnapshots = new();
    private string? _workspaceRoot;
    private int? _historyIndex;
    private bool _recalling;

    public ComposerViewModel(ComposerPromptHistory? history = null)
    {
        _history = history ?? SharedHistory;
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            NotifySendState();
        };
    }

    public ObservableCollection<ComposerImageAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    private string? _inputError;

    public void ConfigureWorkspace(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;
        var normalized = Path.GetFullPath(root);
        if (!string.Equals(_workspaceRoot, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _workspaceRoot = normalized;
            _historyIndex = null;
        }
    }

    public void AddAttachments(IReadOnlyList<ComposerImageAttachment> images)
    {
        if (Attachments.Count + images.Count > 8)
        {
            foreach (var image in images)
                image.Dispose();
            InputError = Loc.Get("composer_attachment_limit");
            return;
        }
        InputError = null;
        foreach (var image in images)
            Attachments.Add(image);
    }

    public void RemoveAttachment(ComposerImageAttachment image)
    {
        if (Attachments.Remove(image))
            image.Dispose();
    }

    public void ClearAttachments()
    {
        foreach (var attachment in Attachments.ToArray())
            RemoveAttachment(attachment);
    }

    public IReadOnlyList<InputImageBlock> SnapshotImages()
    {
        var images = new List<InputImageBlock>(Attachments.Count);
        foreach (var attachment in Attachments)
        {
            var image = attachment.Image;
            _imageSnapshots.Add(image, attachment);
            images.Add(image);
        }
        return images;
    }

    public void RecordSent(string text)
    {
        _historyIndex = null;
        if (_workspaceRoot is null)
            return;
        try
        {
            _history.Record(_workspaceRoot, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            InputError = $"Could not save message history. {exception.Message}";
        }
    }

    public bool RecallHistory(bool previous)
    {
        if (_workspaceRoot is null || HasAttachments || (_historyIndex is null && Draft.Length != 0))
            return false;
        var entries = _history.GetEntries(_workspaceRoot);
        if (entries.Count == 0)
            return false;
        _historyIndex = _historyIndex is not { } index ? entries.Count - 1
            : previous ? Math.Max(0, index - 1) : index + 1;
        var text = _historyIndex >= entries.Count ? string.Empty : entries[_historyIndex.Value];
        if (_historyIndex >= entries.Count)
            _historyIndex = null;
        _recalling = true;
        try
        {
            Draft = text;
        }
        finally
        {
            _recalling = false;
        }
        return true;
    }

    public event Action<PermissionMode>? ModeSelected;

    public event Action<bool>? PlanModeSelected;

    internal void RestoreExecutionState(PermissionMode mode, bool planEnabled)
    {
        _restoringMode = true;
        try
        {
            Mode = mode;
            PlanEnabled = planEnabled;
        }
        finally
        {
            _restoringMode = false;
        }
    }

    internal void RestoreModeOnly(PermissionMode mode)
    {
        _restoringMode = true;
        try
        {
            Mode = mode;
        }
        finally
        {
            _restoringMode = false;
        }
    }

    internal void RestorePlanEnabled(bool planEnabled)
    {
        _restoringMode = true;
        try
        {
            PlanEnabled = planEnabled;
        }
        finally
        {
            _restoringMode = false;
        }
    }

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private PermissionMode _mode = PermissionMode.Yolo;

    [ObservableProperty]
    private bool _isTurnActive;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private PermissionMode _sessionMode = PermissionMode.Yolo;

    [ObservableProperty]
    private bool _planEnabled;

    [ObservableProperty]
    private bool _sessionPlanEnabled;

    // Session-confirmed workflows mirror SessionMode; armed workflows apply to drafts that
    // have not created a session yet.
    [ObservableProperty]
    private IReadOnlyList<string> _sessionWorkflows = [];

    [ObservableProperty]
    private IReadOnlyList<string> _armedWorkflows = [];

    public bool IsOrchestratorActive =>
        SessionWorkflows.Contains(OrchestratorWorkflow.Id) || ArmedWorkflows.Contains(OrchestratorWorkflow.Id);

    public string ModeLabel => Mode switch
    {
        PermissionMode.Yolo => Loc.Get("composer_mode_full_access"),
        PermissionMode.Build => Loc.Get("composer_mode_build"),
        PermissionMode.Edit => Loc.Get("composer_mode_edit"),
        _ => Mode.ToString(),
    };

    public bool IsFullAccess => Mode == PermissionMode.Yolo;

    public bool IsPlanMode => PlanEnabled;

    public bool ShowStop => IsTurnActive && string.IsNullOrWhiteSpace(Draft) && !HasAttachments;

    private bool MatchesSessionState => Mode == SessionMode && PlanEnabled == SessionPlanEnabled;

    public bool CanSend => !IsSending && (!string.IsNullOrWhiteSpace(Draft) || HasAttachments)
        && (!IsTurnActive || MatchesSessionState);

    public string SendHint => IsTurnActive
        ? MatchesSessionState ? Loc.Get("composer_send_hint_queue") : Loc.Get("composer_send_hint_stop_first")
        : Loc.Get("composer_send_hint_send");

    public void ClearIfSendable(string sentText, IReadOnlyList<InputImageBlock>? images = null)
    {
        if (string.Equals(Draft, sentText, StringComparison.Ordinal))
            Draft = string.Empty;
        if (images is not null)
        {
            foreach (var image in images)
            {
                if (_imageSnapshots.TryGetValue(image, out var attachment))
                    RemoveAttachment(attachment);
                _imageSnapshots.Remove(image);
            }
        }
    }

    partial void OnDraftChanged(string value)
    {
        if (!_recalling)
            _historyIndex = null;
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ShowStop));
    }

    partial void OnIsTurnActiveChanged(bool value) => NotifySendState();

    partial void OnIsSendingChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSessionModeChanged(PermissionMode value) => NotifySendState();

    partial void OnSessionPlanEnabledChanged(bool value) => NotifySendState();

    partial void OnSessionWorkflowsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(IsOrchestratorActive));

    partial void OnArmedWorkflowsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(IsOrchestratorActive));

    // Toggling flips armed state for bare "/orchestrator"; arming only adds, so "/orchestrator <prompt>"
    // never deactivates a workflow the prompt explicitly asks for.
    public void ToggleArmedWorkflow(string id)
    {
        ArmedWorkflows = ContainsWorkflow(ArmedWorkflows, id)
            ? ArmedWorkflows.Where(existing => !IsSameWorkflow(existing, id)).ToArray()
            : [.. ArmedWorkflows, id];
    }

    public void ArmWorkflow(string id)
    {
        if (!ContainsWorkflow(ArmedWorkflows, id))
        {
            ArmedWorkflows = [.. ArmedWorkflows, id];
        }
    }

    public static bool ContainsWorkflow(IReadOnlyList<string> workflows, string id)
    {
        foreach (var existing in workflows)
        {
            if (IsSameWorkflow(existing, id))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<string> ToggleWorkflow(IReadOnlyList<string> workflows, string id) =>
        ContainsWorkflow(workflows, id)
            ? workflows.Where(existing => !IsSameWorkflow(existing, id)).ToArray()
            : [.. workflows, id];

    private static bool IsSameWorkflow(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    partial void OnModeChanged(PermissionMode value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsFullAccess));
        NotifySendState();
        if (!_restoringMode)
        {
            ModeSelected?.Invoke(value);
        }
    }

    partial void OnPlanEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlanMode));
        NotifySendState();
        if (!_restoringMode)
        {
            PlanModeSelected?.Invoke(value);
        }
    }

    private void NotifySendState()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendHint));
        OnPropertyChanged(nameof(ShowStop));
    }
}
