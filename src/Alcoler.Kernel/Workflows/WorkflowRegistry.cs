using System.Collections.Concurrent;

namespace Alcoler.Kernel.Workflows;

/// <summary>
/// A session workflow: a slash-triggered, persistent prompt overlay. Definitions are data;
/// the kernel applies the same activation mechanics (system-prompt section plus reminder
/// cadence) to every registered workflow, so new modes ship as definitions, not code paths.
/// </summary>
public sealed record WorkflowDefinition(
    string Id,
    string Title,
    string Description,
    string? SystemSection = null,
    string? FullReminder = null,
    string? SparseReminder = null,
    string? ExitReminder = null);

public sealed class WorkflowRegistry
{
    public const int MaxActiveWorkflows = 3;

    private readonly Dictionary<string, WorkflowDefinition> _byId;

    public WorkflowRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        _byId = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            _byId.TryAdd(definition.Id, definition);
        }
        Definitions = [.. _byId.Values];
    }

    public IReadOnlyList<WorkflowDefinition> Definitions { get; }

    public static WorkflowRegistry BuiltIn { get; } = new([OrchestratorWorkflow.Definition]);

    public WorkflowDefinition? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var definition) ? definition : null;

    // Marker line prefixed to cadence reminders so history scans can attribute a reminder
    // to its workflow regardless of the definition's wording. Exit reminders omit it: an
    // exit marker would otherwise suppress the first cadence attachment on re-activation.
    // Called per attachment part per workflow during history scans; markers are tiny and
    // stable per id, so they are memoized instead of re-interpolated.
    private static readonly ConcurrentDictionary<string, string> MarkerCache = new(StringComparer.Ordinal);

    public static string ReminderMarker(string id) =>
        MarkerCache.GetOrAdd(id, static value => $"<workflow:{value}>");

    public static bool IsReminderFor(string text, string id) =>
        text.Contains(ReminderMarker(id), StringComparison.Ordinal);

    public bool TryNormalize(
        IReadOnlyList<string>? ids,
        out IReadOnlyList<string> normalized,
        out string? error)
    {
        normalized = [];
        if (ids is null || ids.Count == 0)
        {
            error = null;
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        foreach (var raw in ids)
        {
            var id = raw.Trim();
            if (id.Length == 0)
            {
                continue;
            }
            if (Find(id) is not { } definition)
            {
                error = $"unknown workflow '{id}'";
                return false;
            }
            if (seen.Add(id))
            {
                ordered.Add(definition.Id);
            }
        }
        if (ordered.Count > MaxActiveWorkflows)
        {
            error = $"at most {MaxActiveWorkflows} workflows may be active at once";
            return false;
        }
        normalized = ordered;
        error = null;
        return true;
    }

    public string? BuildSystemSection(IReadOnlyList<string>? activeIds)
    {
        if (activeIds is not { Count: > 0 })
        {
            return null;
        }
        List<string> sections = [];
        foreach (var id in activeIds)
        {
            if (Find(id)?.SystemSection is { } section)
            {
                sections.Add(section);
            }
        }
        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }
}
