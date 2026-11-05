using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Localization;

namespace Alcoler.ViewModel;

public sealed partial class WorkbenchViewModel
{
    private ModelSelection? ValidateSelection(ModelSelection? selection) => FindModel(selection) is { } choice
        ? choice.NormalizeSelection(selection!) : WorkbenchModelCatalog.DefaultSelection(_config);

    private ModelSelection? GetSavedSelection(string root, string sessionKey)
    {
        var workspace = _composerConfig.ModelSelections?.FirstOrDefault(pair =>
            string.Equals(pair.Key, DocumentViewModel.NormalizeWorkspaceRoot(root), StringComparison.OrdinalIgnoreCase)).Value;
        return workspace?.TryGetValue(sessionKey, out var selection) == true ? ValidateSelection(selection) : null;
    }

    private ModelSelection? GetNewDraftSelection(string root)
    {
        var recent = _composerConfig.RecentModelSelections?.FirstOrDefault(pair =>
            string.Equals(pair.Key, DocumentViewModel.NormalizeWorkspaceRoot(root), StringComparison.OrdinalIgnoreCase)).Value;
        return ValidateSelection(recent);
    }

    private void ObserveSelection(IComposerHost host, Func<string> sessionKey)
    {
        Action<ModelSelection> observer = selection => SaveModelSelection(host.WorkspaceRoot, sessionKey(), selection);
        _selectionObservers[host] = observer;
        host.ModelSelectionChanged += observer;
    }

    private void UnobserveSelection(IComposerHost host)
    {
        if (_selectionObservers.Remove(host, out var observer))
            host.ModelSelectionChanged -= observer;
    }

    private void UnobserveMode(ComposerViewModel composer)
    {
        if (_modeObservers.Remove(composer, out var observers))
        {
            composer.ModeSelected -= observers.Mode;
            composer.PlanModeSelected -= observers.Plan;
        }
    }

    private Dictionary<string, IReadOnlyDictionary<string, ModelSelection>> CopyModelSelections() =>
        new(_composerConfig.ModelSelections ?? new Dictionary<string, IReadOnlyDictionary<string, ModelSelection>>(),
            StringComparer.OrdinalIgnoreCase);

    private void SaveModelSelection(string root, string sessionKey, ModelSelection selection)
    {
        if (ValidateSelection(selection) is not { } validated)
            return;
        root = DocumentViewModel.NormalizeWorkspaceRoot(root);
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>())
        {
            [sessionKey] = validated,
        };
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces });
    }

    private void TransferDraftSelection(DraftConversationViewModel draft, SessionId sessionId)
    {
        var root = draft.WorkspaceRoot;
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>());
        sessions.Remove(WorkbenchComposerConfig.DraftSessionKey);
        if (ValidateSelection(draft.CurrentSelection) is { } selection)
            sessions[sessionId.Value] = selection;
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces });
    }

    private void RecordAcceptedSelection(string root, SessionId sessionId, ModelSelection selection, long sequence)
    {
        if (ValidateSelection(selection) is not { } validated)
            return;
        root = DocumentViewModel.NormalizeWorkspaceRoot(root);
        var recent = new Dictionary<string, ModelSelection>(
            _composerConfig.RecentModelSelections ?? new Dictionary<string, ModelSelection>(), StringComparer.OrdinalIgnoreCase);
        if (!_recentSubmissionSequences.TryGetValue(root, out var acceptedSequence) || sequence >= acceptedSequence)
        {
            _recentSubmissionSequences[root] = sequence;
            recent[root] = validated;
        }
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>());
        sessions.TryAdd(sessionId.Value, validated);
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces, RecentModelSelections = recent });
    }

    private void ObserveMode(ComposerViewModel composer, Func<SessionId?> sessionId)
    {
        if (_modeObservers.ContainsKey(composer))
            return;
        Action<PermissionMode> modeObserver = mode =>
        {
            if (!Enum.IsDefined(mode))
                return;
            var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes ?? new Dictionary<string, PermissionMode>());
            if (sessionId() is { } id)
                pending[id.Value] = mode;
            SaveComposerConfig(_composerConfig with { DefaultMode = mode, PendingSessionModes = pending });
        };
        Action<bool> planObserver = planEnabled =>
        {
            // Only unsent drafts carry the default plan flag; a session's plan state
            // belongs to the kernel and must not leak into future drafts.
            if (sessionId() is null)
                SaveComposerConfig(_composerConfig with { DefaultPlanEnabled = planEnabled });
        };
        _modeObservers[composer] = (modeObserver, planObserver);
        composer.ModeSelected += modeObserver;
        composer.PlanModeSelected += planObserver;
    }

    private void SavePendingMode(SessionId sessionId, PermissionMode mode)
    {
        if (!Enum.IsDefined(mode))
            return;
        var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes ?? new Dictionary<string, PermissionMode>())
        {
            [sessionId.Value] = mode,
        };
        SaveComposerConfig(_composerConfig with { PendingSessionModes = pending });
    }

    private void ClearSubmittedMode(SessionId sessionId, PermissionMode mode)
    {
        if (_composerConfig.PendingSessionModes?.TryGetValue(sessionId.Value, out var pendingMode) != true || pendingMode != mode)
            return;
        var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes);
        pending.Remove(sessionId.Value);
        SaveComposerConfig(_composerConfig with { PendingSessionModes = pending });
    }

    private void SaveComposerConfig(WorkbenchComposerConfig config)
    {
        _composerConfig = config;
        try
        {
            _saveComposer?.Invoke(config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Banner = Loc.Get("workbench_composer_save_error", exception.Message);
        }
    }

    private ModelChoice? FindModel(ModelSelection? selection) => selection is null ? null : ModelChoices.FirstOrDefault(choice =>
        choice.ProviderId == selection.ProviderId && choice.ModelId == selection.ModelId);
}
