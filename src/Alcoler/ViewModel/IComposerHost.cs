using System.ComponentModel;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Models;

namespace Alcoler.ViewModel;

public interface IComposerHost : INotifyPropertyChanged
{
    ComposerViewModel Composer { get; }

    string WorkspaceRoot { get; }

    ModelSelection? CurrentSelection { get; }

    event Action<ModelSelection>? ModelSelectionChanged;

    string ModelText { get; }

    IReadOnlyList<string> ThoughtLevels { get; }

    string ThoughtLevel { get; set; }

    ContextUsageViewModel? ContextUsage { get; }

    IReadOnlyList<QueuedInput> Queue { get; }

    bool HasQueuedInput { get; }

    Task SendAsync();

    Task WithdrawQueuedInputAsync(QueuedInput input);

    Task DeleteQueuedInputAsync(QueuedInput input);

    Task StopAsync();

    Task CompactAsync();

    Task SwitchModelAsync(ModelSelection selection);

    ModelSelection ApplyThoughtLevel(ModelSelection selection);

    void ConfigureModel(ModelChoice choice, bool useDefaultReasoning = false);
}
