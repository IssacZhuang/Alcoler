namespace Alcoler.Kernel.Prompts;

public enum AgentInstructionScope
{
    Workspace,
    User,
}

public sealed record AgentInstructionSource(
    string FilePath,
    string FileName,
    string Content,
    bool Truncated,
    AgentInstructionScope Scope);

public static class AgentsMdSection
{
    public const string Heading = "# agentsMd";

    public const string Lead = "Codebase and user instructions are shown below. Be sure to adhere to these instructions. IMPORTANT: These instructions OVERRIDE any default behavior and you MUST follow them exactly as written.";

    public static string? Render(IEnumerable<AgentInstructionSource> sources)
    {
        var blocks = sources
            .Select(RenderSource)
            .Where(block => block is not null)
            .ToList();
        if (blocks.Count == 0)
            return null;
        return string.Join("\n", Heading, Lead, "", string.Join("\n\n", blocks!));
    }

    private static string? RenderSource(AgentInstructionSource source)
    {
        var body = source.Truncated
            ? source.Content + $"\n\n[File truncated: {source.FileName}]"
            : source.Content;
        var trimmedBody = body.Trim();
        return trimmedBody.Length == 0
            ? null
            : $"Contents of {source.FilePath} ({ScopeLabel(source.Scope)}):\n\n{trimmedBody}";
    }

    private static string ScopeLabel(AgentInstructionScope scope) =>
        scope is AgentInstructionScope.User ? "user default instructions" : "workspace instructions";
}
