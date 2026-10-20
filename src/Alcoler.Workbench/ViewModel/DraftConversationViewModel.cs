using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class DraftConversationViewModel : ObservableObject, IComposerHost
{
    private readonly Func<DraftConversationViewModel, Task> _send;
    private ModelSelection? _selection;

    public DraftConversationViewModel(string workspaceRoot, ModelChoice? model, Func<DraftConversationViewModel, Task> send)
    {
        WorkspaceRoot = workspaceRoot;
        _send = send;
        if (model is not null)
        {
            _selection = model.Selection;
            ConfigureModel(model);
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

    public ModelSelection? Selection => _selection is { } selection ? ApplyThoughtLevel(selection) : null;

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

    public Task StopAsync() => Task.CompletedTask;

    public Task CompactAsync() => Task.CompletedTask;

    public Task SwitchModelAsync(ModelSelection selection)
    {
        _selection = selection;
        ModelText = $"{selection.ProviderId}/{selection.ModelId}";
        return Task.CompletedTask;
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
        ThoughtLevels = choice.Properties?.ReasoningLevels ?? [];
        if (useDefaultReasoning || !ThoughtLevels.Contains(ThoughtLevel))
        {
            ThoughtLevel = (useDefaultReasoning ? ThoughtLevels.LastOrDefault() : ThoughtLevels.FirstOrDefault()) ?? "off";
        }
        ModelText = $"{choice.ProviderId}/{choice.ModelId}";
    }
}
