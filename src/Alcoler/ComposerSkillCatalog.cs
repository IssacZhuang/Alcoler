using Alcoler.Kernel.Skills;

namespace Alcoler;

public sealed record ComposerSkillEntry(string Name, string Description, string Path);

public sealed class ComposerSkillCatalog(SkillsCatalog? catalog = null)
{
    private readonly SkillsCatalog _catalog = catalog ?? new SkillsCatalog();

    public IReadOnlyList<ComposerSkillEntry> GetEntries(
        string workspaceRoot, IReadOnlyList<string>? disabledSkillPaths = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return [];
        }
        try
        {
            return _catalog.Load(Path.GetFullPath(workspaceRoot), disabledSkillPaths)
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(skill => skill.Name, StringComparer.Ordinal)
                .Select(skill => new ComposerSkillEntry(skill.Name, skill.Description, skill.SkPath))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<ComposerSkillEntry> Query(
        string workspaceRoot, string text, IReadOnlyList<string>? disabledSkillPaths = null)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
        {
            return [];
        }
        var query = text[1..];
        if (query.Any(char.IsWhiteSpace))
        {
            return [];
        }
        return GetEntries(workspaceRoot, disabledSkillPaths)
            .Where(skill => skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
