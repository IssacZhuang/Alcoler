using System.Text;

namespace Alcoler.Kernel.Skills;

public enum SkillSource
{
    Workspace,
    User,
}

public sealed record SkillInfo(
    string Name,
    string Description,
    SkillSource Source,
    string SkPath,
    string? WhenToUse = null,
    string? Alias = null);

/// <summary>
/// Loads skills from {workspaceRoot}/.alcoler/skills and {userProfile}/.alcoler/skills.
/// Workspace skills shadow user skills with the same name; malformed entries are skipped.
/// </summary>
public sealed class SkillsCatalog
{
    private const string SkillFileName = "SKILL.md";
    private const string ListingHeader = "Available skills:";

    private readonly string _userSkillsRoot;

    public SkillsCatalog(string? userSkillsRoot = null) =>
        _userSkillsRoot = userSkillsRoot ?? DefaultUserSkillsRoot();

    public static string DefaultUserSkillsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alcoler", "skills");

    public IReadOnlyList<SkillInfo> Load(string workspaceRoot)
    {
        var byName = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in LoadFrom(Path.Combine(workspaceRoot, ".alcoler", "skills"), SkillSource.Workspace))
            byName.TryAdd(skill.Name, skill);
        foreach (var skill in LoadFrom(_userSkillsRoot, SkillSource.User))
            byName.TryAdd(skill.Name, skill);
        return [.. byName.Values];
    }

    public static string RenderListing(IReadOnlyList<SkillInfo> skills)
    {
        if (skills.Count == 0)
            return string.Empty;
        var builder = new StringBuilder(ListingHeader);
        foreach (var skill in skills)
        {
            builder.Append('\n').Append("- ").Append(skill.Name).Append(": ")
                .Append(skill.Description)
                .Append(" (").Append(skill.Source is SkillSource.Workspace ? "workspace" : "user").Append(')');
        }
        return builder.ToString();
    }

    public static string StripFrontMatterForTool(string text) =>
        StripFrontMatter(text, out _);

    private static IReadOnlyList<SkillInfo> LoadFrom(string root, SkillSource source)
    {
        if (!Directory.Exists(root))
            return [];

        var skills = new List<SkillInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var skillPath = Path.Combine(directory, SkillFileName);
            if (TryReadAllText(skillPath) is not { } text)
                continue;
            if (Parse(text, skillPath, source) is { } skill)
                skills.Add(skill);
        }
        return skills;
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
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

    internal static SkillInfo? Parse(string text, string skillPath, SkillSource source)
    {
        var body = StripFrontMatter(text, out var frontMatter);

        string? name = null;
        string? description = null;
        string? whenToUse = null;
        if (frontMatter is not null)
        {
            name = FrontMatterValue(frontMatter, "name");
            description = FrontMatterValue(frontMatter, "description");
            whenToUse = FrontMatterValue(frontMatter, "when_to_use");
        }
        if (name is null || description is null)
        {
            var (headingName, headingDescription) = ParseHeadingFallback(body);
            name ??= headingName;
            description ??= headingDescription;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
            return null;
        return new SkillInfo(name.Trim(), description.Trim(), source, skillPath, NormalizeOptionalValue(whenToUse));
    }

    private static string? NormalizeOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string StripFrontMatter(string text, out IReadOnlyList<string>? frontMatter)
    {
        frontMatter = null;
        var lines = text.TrimStart('\uFEFF').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return text;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "---")
                continue;
            frontMatter = lines[1..i];
            return string.Join('\n', lines[(i + 1)..]);
        }
        return text;
    }

    private static string? FrontMatterValue(IReadOnlyList<string> lines, string key)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            var separator = line.IndexOf(':');
            if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = line[(separator + 1)..].Trim();
            if (value is ">" or ">-" or ">+" or "|" or "|-" or "|+")
                return ReadBlockScalar(lines, index + 1, value[0] == '>');
            if (value.Length >= 2
                && ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
                value = value[1..^1].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string? ReadBlockScalar(IReadOnlyList<string> lines, int start, bool folded)
    {
        var block = lines.Skip(start).TakeWhile(line => string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0])).ToList();
        var indent = block.Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.TakeWhile(char.IsWhiteSpace).Count()).DefaultIfEmpty(0).Min();
        var content = block.Select(line => string.IsNullOrWhiteSpace(line) ? "" : line[indent..].TrimEnd('\r')).ToList();
        if (!folded)
            return NormalizeOptionalValue(string.Join('\n', content));
        var paragraphs = new List<string>();
        var paragraph = new List<string>();
        foreach (var line in content.Append(""))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                paragraph.Add(line.Trim());
                continue;
            }
            if (paragraph.Count == 0)
                continue;
            paragraphs.Add(string.Join(' ', paragraph));
            paragraph.Clear();
        }
        return NormalizeOptionalValue(string.Join('\n', paragraphs));
    }

    private static (string? Name, string? Description) ParseHeadingFallback(string body)
    {
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("# ", StringComparison.Ordinal))
                continue;
            var name = line[2..].Trim();
            return (name.Length == 0 ? null : name, ParagraphAfter(lines, i + 1));
        }
        return (null, null);
    }

    private static string? ParagraphAfter(string[] lines, int start)
    {
        var index = start;
        while (index < lines.Length && lines[index].Trim().Length == 0)
            index++;

        var paragraph = new List<string>();
        while (index < lines.Length && lines[index].Trim().Length > 0)
        {
            paragraph.Add(lines[index].Trim());
            index++;
        }
        return paragraph.Count == 0 ? null : string.Join(" ", paragraph);
    }
}
