using Alcoler.Kernel.Skills;

namespace Alcoler.Kernel.Prompts;

public static class SkillsListing
{
    public const string Header = "The following skills are available for use with the Skill tool:";

    public const int DefaultMetadataBudget = 20_000;

    private const int MaxDescriptionChars = 250;

    public static string? Render(IReadOnlyList<SkillInfo> skills, int metadataBudget = DefaultMetadataBudget)
    {
        if (skills.Count == 0)
            return null;
        // The reference sorts with localeCompare; ordinal keeps the listing deterministic across machines.
        var sorted = skills.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList();
        var full = string.Join("\n", new[] { Header, "" }.Concat(sorted.Select(FormatLine)));
        if (full.Length <= metadataBudget)
            return full;
        return string.Join("\n", new[] { Header, "" }.Concat(sorted.Select(FormatNameOnlyLine)));
    }

    private static string FormatLine(SkillInfo skill)
    {
        var description = skill.WhenToUse is { Length: > 0 }
            ? $"{skill.Description} - {skill.WhenToUse}"
            : skill.Description;
        var trimmed = description.Length > MaxDescriptionChars
            ? description[..(MaxDescriptionChars - 1)] + "..."
            : description;
        return $"- {skill.Name}: {trimmed}{AliasSuffix(skill)} (file: {skill.SkPath})";
    }

    private static string FormatNameOnlyLine(SkillInfo skill) =>
        $"- {skill.Name}{AliasSuffix(skill)} (file: {skill.SkPath})";

    private static string AliasSuffix(SkillInfo skill) =>
        skill.Alias is { Length: > 0 } && skill.Alias != skill.Name
            ? $" (also loadable as {skill.Alias})"
            : "";
}
