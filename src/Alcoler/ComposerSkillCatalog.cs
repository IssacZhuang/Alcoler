using Alcoler.Kernel.Skills;

namespace Alcoler;

public sealed record ComposerSkillEntry(string Name, string Description, string Path);

public sealed class ComposerSkillCatalog(SkillsCatalog? catalog = null)
{
    private readonly SkillsCatalog _catalog = catalog ?? new SkillsCatalog();
    private string? _cachedRoot;
    private IReadOnlyList<string>? _cachedDisabled;
    private ComposerSkillEntry[] _cachedEntries = [];

    public IReadOnlyList<ComposerSkillEntry> GetEntries(
        string workspaceRoot, IReadOnlyList<string>? disabledSkillPaths = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return [];
        }
        try
        {
            return LoadEntries(workspaceRoot, disabledSkillPaths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private ComposerSkillEntry[] LoadEntries(string workspaceRoot, IReadOnlyList<string>? disabledSkillPaths) =>
        _catalog.Load(Path.GetFullPath(workspaceRoot), disabledSkillPaths)
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Name, StringComparer.Ordinal)
            .Select(skill => new ComposerSkillEntry(skill.Name, skill.Description, skill.SkPath))
            .ToArray();

    private ComposerSkillEntry[] CachedEntries(string workspaceRoot, IReadOnlyList<string>? disabledSkillPaths)
    {
        string root;
        try
        {
            root = Path.GetFullPath(workspaceRoot);
            if (string.Equals(root, _cachedRoot, StringComparison.Ordinal)
                && SameDisabledPaths(disabledSkillPaths, _cachedDisabled))
            {
                return _cachedEntries;
            }
            _cachedEntries = LoadEntries(root, disabledSkillPaths);
            _cachedRoot = root;
            _cachedDisabled = disabledSkillPaths;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return _cachedEntries;
    }

    private static bool SameDisabledPaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public IReadOnlyList<ComposerSkillEntry> Query(
        string workspaceRoot, string text, IReadOnlyList<string>? disabledSkillPaths = null)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
        {
            return [];
        }
        var query = text[1..];
        for (var index = 0; index < query.Length; index++)
        {
            if (char.IsWhiteSpace(query[index]))
                return [];
        }
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return [];

        var entries = CachedEntries(workspaceRoot, disabledSkillPaths);
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        if (count == 0)
            return [];
        var matches = new ComposerSkillEntry[count];
        var matchIndex = 0;
        foreach (var entry in entries)
        {
            if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches[matchIndex++] = entry;
        }
        return matches;
    }
}
