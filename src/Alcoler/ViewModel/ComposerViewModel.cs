using System.Collections.ObjectModel;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
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
            InputError = "You can attach up to 8 images.";
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

    internal void RestoreMode(PermissionMode mode)
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

    public string ModeLabel => Mode == PermissionMode.Yolo ? "Full access" : Mode.ToString();

    public bool IsFullAccess => Mode == PermissionMode.Yolo;

    public bool ShowStop => IsTurnActive && string.IsNullOrWhiteSpace(Draft) && !HasAttachments;

    public bool CanSend => !IsSending && (!string.IsNullOrWhiteSpace(Draft) || HasAttachments)
        && (!IsTurnActive || Mode == SessionMode);

    public string SendHint => IsTurnActive
        ? Mode == SessionMode ? "Queue message (Enter)" : "Stop the active turn before changing permissions"
        : "Send message (Enter)";

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

    private void NotifySendState()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendHint));
        OnPropertyChanged(nameof(ShowStop));
    }
}
