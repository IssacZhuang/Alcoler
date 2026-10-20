using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Skills;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Resolves skill bodies for the Skill tool from the same catalog that produced the
/// session's skills listing; the listing and the tool must agree on shadowing rules.
/// </summary>
internal sealed class SkillContentProvider(string workspaceRoot) : ISkillContentProvider
{
    private readonly SkillsCatalog _catalog = new();
    private readonly Dictionary<string, SkillInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;

    public SkillDocument? TryLoad(string name, CancellationToken cancellationToken)
    {
        LoadOnce();
        if (!_byName.TryGetValue(name.Trim(), out var skill))
            return null;

        try
        {
            var content = File.ReadAllText(skill.SkPath);
            return new SkillDocument(
                skill.Name,
                skill.Description,
                Path.GetDirectoryName(skill.SkPath)!,
                SkillsCatalog.StripFrontMatterForTool(content));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LoadOnce()
    {
        if (_loaded)
            return;
        foreach (var skill in _catalog.Load(workspaceRoot))
            _byName.TryAdd(skill.Name, skill);
        _loaded = true;
    }
}
