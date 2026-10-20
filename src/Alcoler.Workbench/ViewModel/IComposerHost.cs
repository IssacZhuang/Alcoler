using System.ComponentModel;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Models;

namespace Alcoler.Workbench.ViewModel;

public interface IComposerHost : INotifyPropertyChanged
{
    ComposerViewModel Composer { get; }

    string ModelText { get; }

    IReadOnlyList<string> ThoughtLevels { get; }

    string ThoughtLevel { get; set; }

    ContextUsageViewModel? ContextUsage { get; }

    IReadOnlyList<QueuedInput> Queue { get; }

    bool HasQueuedInput { get; }

    Task SendAsync();

    Task StopAsync();

    Task CompactAsync();

    Task SwitchModelAsync(ModelSelection selection);

    ModelSelection ApplyThoughtLevel(ModelSelection selection);

    void ConfigureModel(ModelChoice choice, bool useDefaultReasoning = false);
}
