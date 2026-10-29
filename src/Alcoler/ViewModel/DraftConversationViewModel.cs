using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class DraftConversationViewModel : ObservableObject, IComposerHost
{
    private readonly Func<DraftConversationViewModel, Task> _send;
    private ModelSelection? _selection;
    private ModelChoice? _configuredModel;
    private bool _configuringModel;

    public DraftConversationViewModel(string workspaceRoot, ModelChoice? model, Func<DraftConversationViewModel, Task> send)
    {
        WorkspaceRoot = workspaceRoot;
        Composer.ConfigureWorkspace(workspaceRoot);
        _send = send;
        if (model is not null)
        {
            ConfigureModel(model);
            RestoreModelSelection(ApplyThoughtLevel(model.Selection));
        }
    }

    public string WorkspaceRoot { get; }

    public string WorkspaceName => string.Equals(WorkspaceRoot, WorkbenchViewModel.OutsideProjectRoot, StringComparison.OrdinalIgnoreCase)
        ? "Outside project"
        : Path.GetFileName(WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : WorkspaceRoot;

    public ComposerViewModel Composer { get; } = new();

    public ContextUsageViewModel? ContextUsage => null;

    public IReadOnlyList<QueuedInput> Queue => [];

    public bool HasQueuedInput => false;

    public ModelSelection? CurrentSelection => _selection;

    public ModelSelection? Selection => CurrentSelection;

    public event Action<ModelSelection>? ModelSelectionChanged;

    internal SessionId? CreatedSessionId { get; set; }

    public string? UnsentText { get; internal set; }

    [ObservableProperty]
    private string _modelText = "default model";

    [ObservableProperty]
    private IReadOnlyList<string> _thoughtLevels = [];

    [ObservableProperty]
    private string _thoughtLevel = "off";

    [ObservableProperty]
    private string? _banner;

    public Task SendAsync() => _send(this);

    public Task SendQueuedNowAsync(QueuedInput input) => Task.CompletedTask;

    public Task WithdrawQueuedInputAsync(QueuedInput input) => Task.CompletedTask;

    public Task DeleteQueuedInputAsync(QueuedInput input) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public Task CompactAsync() => Task.CompletedTask;

    public Task SwitchModelAsync(ModelSelection selection)
    {
        RestoreModelSelection(selection);
        ModelSelectionChanged?.Invoke(_selection!);
        return Task.CompletedTask;
    }

    internal void RestoreModelSelection(ModelSelection selection)
    {
        _selection = _configuredModel is { } model && model.ProviderId == selection.ProviderId && model.ModelId == selection.ModelId
            ? model.NormalizeSelection(selection) : selection;
        _configuringModel = true;
        try
        {
            ThoughtLevel = _selection.Options?.ReasoningLevel ?? "off";
        }
        finally
        {
            _configuringModel = false;
        }
        ModelText = $"{selection.ProviderId}/{selection.ModelId}";
        OnPropertyChanged(nameof(CurrentSelection));
        OnPropertyChanged(nameof(Selection));
    }

    internal void UpdateModelChoices(IReadOnlyList<ModelChoice> modelChoices, ModelSelection? defaultSelection)
    {
        if (_selection is not { } selection)
        {
            return;
        }
        var choice = modelChoices.FirstOrDefault(model =>
            model.ProviderId == selection.ProviderId && model.ModelId == selection.ModelId);
        if (choice is not null)
        {
            ConfigureModel(choice);
            return;
        }

        var fallback = defaultSelection is { } desired
            ? modelChoices.FirstOrDefault(model =>
                model.ProviderId == desired.ProviderId && model.ModelId == desired.ModelId)
            : null;
        fallback ??= modelChoices.FirstOrDefault();
        if (fallback is null)
        {
            _configuredModel = null;
            _selection = null;
            ModelText = Loc.Get("composer_default_model");
            ThoughtLevels = [];
            ThoughtLevel = "off";
            OnPropertyChanged(nameof(CurrentSelection));
            OnPropertyChanged(nameof(Selection));
            return;
        }
        ConfigureModel(fallback, useDefaultReasoning: true);
        RestoreModelSelection(ApplyThoughtLevel(fallback.Selection));
        ModelSelectionChanged?.Invoke(_selection!);
    }

    public ModelSelection ApplyThoughtLevel(ModelSelection selection) => selection with
    {
        Options = (selection.Options ?? new ModelCallOptions()) with
        {
            ReasoningLevel = ThoughtLevel == "" || ThoughtLevel == "off" && !ThoughtLevels.Contains("off") ? null : ThoughtLevel,
        },
    };

    public void ConfigureModel(ModelChoice choice, bool useDefaultReasoning = false)
    {
        _configuredModel = choice;
        _configuringModel = true;
        try
        {
            ThoughtLevels = choice.Properties?.ReasoningLevels ?? [];
            if (useDefaultReasoning || !ThoughtLevels.Contains(ThoughtLevel))
            {
                ThoughtLevel = (useDefaultReasoning ? ThoughtLevels.LastOrDefault() : ThoughtLevels.FirstOrDefault()) ?? "off";
            }
        }
        finally
        {
            _configuringModel = false;
        }
        if (_selection is { } selection && selection.ProviderId == choice.ProviderId && selection.ModelId == choice.ModelId)
        {
            var normalized = ApplyThoughtLevel(choice.NormalizeSelection(selection));
            if (normalized != selection)
            {
                RestoreModelSelection(normalized);
                ModelSelectionChanged?.Invoke(normalized);
            }
        }
    }

    partial void OnThoughtLevelChanged(string value)
    {
        if (_configuringModel || _selection is not { } selection)
            return;
        if (_configuredModel is not null && !ThoughtLevels.Contains(value))
        {
            _configuringModel = true;
            ThoughtLevel = selection.Options?.ReasoningLevel ?? "off";
            _configuringModel = false;
            return;
        }
        _ = SwitchModelAsync(ApplyThoughtLevel(selection));
    }
}
