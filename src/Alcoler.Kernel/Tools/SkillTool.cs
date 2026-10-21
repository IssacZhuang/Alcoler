using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Loads one skill body from ISkillContentProvider into the conversation. Session-level
/// deduplication is not performed; the model is responsible for not re-invoking a skill
/// it already loaded. Content beyond MaxSkillBytes is truncated.
/// </summary>
public sealed class SkillTool : ITool
{
    public const int MaxSkillBytes = 100_000;

    private const string TruncationMarker = "[Skill content truncated]";

    public ToolContract Contract { get; } = new(
        "Skill",
        """
        Execute a skill within the main conversation

        When users ask you to perform tasks, check if any of the available skills match. Skills provide specialized capabilities and domain knowledge.

        When users reference a "slash command" or "/<something>", they are referring to a skill. Use this tool to invoke it.

        How to invoke:
        - Set `skill` to the exact name of an available skill (no leading slash). For plugin-namespaced skills use the fully qualified `plugin:skill` form.
        - Set `args` to pass optional arguments.

        Important:
        - Available skills are listed in system-reminder messages in the conversation
        - Only invoke a skill that appears in that list, or one the user explicitly typed as `/<name>` in their message. Never guess or invent a skill name from training data; otherwise do not call this tool
        - When a skill matches the user's request, this is a BLOCKING REQUIREMENT: invoke the relevant Skill tool BEFORE generating any other response about the task
        - NEVER mention a skill without actually calling this tool
        - Do not invoke a skill that is already running
        - If you see a <command-name> tag in the current conversation turn, the skill has ALREADY been loaded - follow the instructions directly instead of calling this tool again
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "skill": { "type": "string", "description": "The name of a skill from the available-skills list. Do not guess names." },
            "args": { "type": "string", "description": "Optional arguments for the skill" }
          },
          "required": ["skill"],
          "additionalProperties": false
        }
        """));

    public JsonElement RuntimeInputSchema { get; } = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "skill": { "type": "string" },
            "name": { "type": "string" },
            "args": { "type": "string" }
          },
          "anyOf": [ { "required": ["skill"] }, { "required": ["name"] } ],
          "additionalProperties": false
        }
        """);

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.ValueKind is not JsonValueKind.Object
            || !TryReadSkillName(input, out var name)
            || string.IsNullOrWhiteSpace(name))
            return ValueTask.FromResult<ToolInputIssue?>(new("missing_skill", "'skill' must be a non-empty string"));

        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        // Legacy {name, args} input is rewritten to {skill, args} before execution and permission checks.
        if (input.ValueKind is not JsonValueKind.Object
            || !input.TryGetProperty("name", out var name)
            || name.ValueKind is not JsonValueKind.String
            || input.TryGetProperty("skill", out _))
            return ValueTask.FromResult(input);

        var rewritten = new JsonObject();
        foreach (var property in input.EnumerateObject())
        {
            if (property.NameEquals("name"))
                continue;
            rewritten[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        rewritten["skill"] = name.GetString();
        using var document = JsonDocument.Parse(rewritten.ToJsonString());
        return ValueTask.FromResult(document.RootElement.Clone());
    }

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Skills is null)
            return ValueTask.FromResult(ToolExecutionResult.Text(
                "Skill tool is not available in this session", isError: true));
        if (!TryReadSkillName(context.Input, out var name) || string.IsNullOrWhiteSpace(name))
            return ValueTask.FromResult(ToolExecutionResult.Text(
                "'skill' must be a non-empty string", isError: true));

        var document = context.Skills.TryLoad(name, cancellationToken);
        if (document is null)
            return ValueTask.FromResult(ToolExecutionResult.Text(
                $"Skill not found: {name}",
                isError: true));

        return ValueTask.FromResult(ToolExecutionResult.Text(Render(document)));
    }

    private static bool TryReadSkillName(JsonElement input, out string? name)
    {
        if (input.TryGetProperty("skill", out var skill) && skill.ValueKind is JsonValueKind.String)
        {
            name = skill.GetString();
            return true;
        }
        if (input.TryGetProperty("name", out var legacy) && legacy.ValueKind is JsonValueKind.String)
        {
            name = legacy.GetString();
            return true;
        }
        name = null;
        return false;
    }

    internal static string Render(SkillDocument document)
    {
        var truncated = document.Truncated || Encoding.UTF8.GetByteCount(document.Content) > MaxSkillBytes;
        var content = Encoding.UTF8.GetByteCount(document.Content) > MaxSkillBytes
            ? Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(document.Content).AsSpan(0, MaxSkillBytes))
            : document.Content;
        content = Regex.Replace(content, @"\$\{(?:CLAUDE_SKILL_DIR|ZCODE_SKILL_DIR)\}", _ => document.BaseDirectory);

        var builder = new StringBuilder(content.Length + 256);
        builder.Append("<skill_content name=\"").Append(document.Name).Append("\">\n");
        builder.Append("# Skill: ").Append(document.Name);
        if (content.Length > 0)
            builder.Append('\n').Append(content);
        builder.Append('\n')
            .Append("Base directory for this skill: ").Append(document.BaseDirectory).Append('\n')
            .Append("Relative paths in this skill are relative to this base directory.");
        if (truncated)
            builder.Append('\n').Append(TruncationMarker);
        builder.Append('\n').Append("</skill_content>");
        return builder.ToString();
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
