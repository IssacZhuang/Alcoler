using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public sealed record ToolSchedulerGroup(bool Parallel, IReadOnlyList<ScheduledToolCall> Calls);

public sealed class ToolScheduler
{
    public const int MaxConcurrency = 10;

    private readonly ToolRegistry _registry;

    public ToolScheduler(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public static bool CanRunParallel(ITool? tool) =>
        tool is not null
        && !tool.Metadata.Destructive
        && tool.Metadata.ConcurrentSafe
        && tool.Metadata.SideEffectScope == SideEffectScope.None;

    public IReadOnlyList<ToolSchedulerGroup> Schedule(IReadOnlyList<ScheduledToolCall> calls)
    {
        var groups = new List<ToolSchedulerGroup>();
        List<ScheduledToolCall>? current = null;
        var currentParallel = false;

        foreach (var call in calls)
        {
            var parallel = CanRunParallel(_registry.TryFind(call.Call.Name));
            if (current is null || !parallel || !currentParallel || current.Count >= MaxConcurrency)
            {
                if (current is not null)
                    groups.Add(new ToolSchedulerGroup(currentParallel, current));
                current = [];
                currentParallel = parallel;
            }
            current.Add(call);
        }

        if (current is not null)
            groups.Add(new ToolSchedulerGroup(currentParallel, current));

        return groups;
    }
}
