using System.Text;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Skills;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Resolves skill bodies for the Skill tool from the same catalog that produced the
/// session's skills listing; the listing and the tool must agree on shadowing rules.
/// </summary>
internal sealed class SkillContentProvider(string workspaceRoot, string? userSkillsBase = null) : ISkillContentProvider
{
    private readonly SkillsCatalog _catalog = new(userSkillsBase);
    public SkillDocument? TryLoad(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var skill = _catalog.Load(workspaceRoot).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Alias, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return null;

        try
        {
            using var stream = File.OpenRead(skill.SkPath);
            var truncated = stream.Length > Tools.SkillTool.MaxSkillBytes;
            var bytes = new byte[(int)Math.Min(stream.Length, Tools.SkillTool.MaxSkillBytes)];
            stream.ReadExactly(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            var content = Encoding.UTF8.GetString(bytes);
            return new SkillDocument(
                skill.Name,
                skill.Description,
                Path.GetDirectoryName(skill.SkPath)!,
                SkillsCatalog.StripFrontMatterForTool(content).Trim(),
                truncated);
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

}
