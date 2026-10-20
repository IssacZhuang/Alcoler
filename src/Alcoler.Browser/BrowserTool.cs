using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Browser;

/// <summary>
/// Single "Browser" tool: the "action" property selects the verb. Navigation and
/// interaction results return as plain tool results; the screenshot png never
/// enters the model-facing text, it is exposed through the status surface.
/// </summary>
public sealed class BrowserTool : ITool
{
    private const int MaxTextExcerptLength = 4000;
    private const int MaxWaitMilliseconds = 60_000;

    private static readonly string[] Verbs =
        ["navigate", "click", "type", "screenshot", "press", "scroll", "wait", "read_text"];

    private readonly IBrowserController _controller;

    public BrowserTool(IBrowserController controller) => _controller = controller;

    public ToolContract Contract { get; } = new(
        "Browser",
        "Controls a headed Chromium window: navigates, clicks, types, presses keys, scrolls, waits, reads page text and takes screenshots. The window is visible to the user; screenshots surface in the browser pane, not in the tool result text.",
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["navigate", "click", "type", "screenshot", "press", "scroll", "wait", "read_text"], "description": "Verb to run in the browser." },
            "url": { "type": "string", "description": "Target URL for navigate." },
            "selector": { "type": "string", "description": "CSS selector for click/type." },
            "text": { "type": "string", "description": "Text to type into the selector." },
            "submit": { "type": "boolean", "description": "Press Enter after typing; defaults to false." },
            "key": { "type": "string", "description": "Key or key combination for press, e.g. Enter, Control+A." },
            "deltaX": { "type": "integer", "description": "Horizontal scroll amount for scroll; defaults to 0." },
            "deltaY": { "type": "integer", "description": "Vertical scroll amount for scroll; positive scrolls down; defaults to 0." },
            "milliseconds": { "type": "integer", "description": "How long to wait, at most 60000." }
          },
          "required": ["action"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.System,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (ParseAction(input, out _, out var problem))
        {
            return ValueTask.FromResult<ToolInputIssue?>(null);
        }
        return ValueTask.FromResult<ToolInputIssue?>(problem);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!ParseAction(context.Input, out var parsed, out var problem))
        {
            return ToolExecutionResult.Text(problem!.Message, isError: true);
        }
        var action = parsed!;
        var result = await _controller.ExecuteAsync(context.SessionId, action, cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Text(Summarize(action, result), isError: !result.Ok);
    }

    internal static string Summarize(BrowserAction action, BrowserActionResult result)
    {
        if (!result.Ok)
        {
            return $"Browser {action.Verb} failed: {result.Error}";
        }
        var where = result.Title is null ? result.Url : $"{result.Url} — \"{Truncate(result.Title, 200)}\"";
        var summary = action switch
        {
            BrowserScreenshotAction => $"Screenshot captured of {where}; it is shown in the browser pane.",
            BrowserWaitAction => $"Waited {((BrowserWaitAction)action).Milliseconds} ms on {where}.",
            BrowserReadTextAction => $"Text of {where}:",
            BrowserNavigateAction => $"Navigated to {where}.",
            _ => $"{char.ToUpperInvariant(action.Verb[0])}{action.Verb[1..]} done on {where}.",
        };
        if (action is BrowserReadTextAction or BrowserNavigateAction && !string.IsNullOrEmpty(result.Text))
        {
            summary += $"\n{Truncate(result.Text, MaxTextExcerptLength)}";
        }
        return summary;
    }

    private static bool ParseAction(JsonElement input, out BrowserAction? action, out ToolInputIssue? issue)
    {
        action = null;
        issue = default;
        if (input.ValueKind is not JsonValueKind.Object
            || !input.TryGetProperty("action", out var actionElement)
            || actionElement.ValueKind is not JsonValueKind.String)
        {
            issue = new ToolInputIssue("missing_action", "'action' must be one of: " + string.Join(", ", Verbs));
            return false;
        }
        var verb = actionElement.GetString()!;
        switch (verb)
        {
            case "navigate":
                if (!TryGetNonEmptyString(input, "url", out var url))
                {
                    issue = new ToolInputIssue("missing_url", "'navigate' requires a non-empty 'url'");
                    return false;
                }
                action = new BrowserNavigateAction(url);
                return true;
            case "click":
                if (!TryGetNonEmptyString(input, "selector", out var clickSelector))
                {
                    issue = new ToolInputIssue("missing_selector", "'click' requires a non-empty 'selector'");
                    return false;
                }
                action = new BrowserClickAction(clickSelector);
                return true;
            case "type":
                if (!TryGetNonEmptyString(input, "selector", out var typeSelector))
                {
                    issue = new ToolInputIssue("missing_selector", "'type' requires a non-empty 'selector'");
                    return false;
                }
                if (!input.TryGetProperty("text", out var textElement)
                    || textElement.ValueKind is not JsonValueKind.String)
                {
                    issue = new ToolInputIssue("missing_text", "'type' requires a string 'text' (empty string allowed)");
                    return false;
                }
                var submit = input.TryGetProperty("submit", out var submitElement)
                    && submitElement.ValueKind is JsonValueKind.True;
                action = new BrowserTypeAction(typeSelector, textElement.GetString()!, submit);
                return true;
            case "screenshot":
                action = new BrowserScreenshotAction();
                return true;
            case "press":
                if (!TryGetNonEmptyString(input, "key", out var key))
                {
                    issue = new ToolInputIssue("missing_key", "'press' requires a non-empty 'key'");
                    return false;
                }
                action = new BrowserPressAction(key);
                return true;
            case "scroll":
                action = new BrowserScrollAction(
                    GetInt32OrDefault(input, "deltaX"),
                    GetInt32OrDefault(input, "deltaY"));
                return true;
            case "wait":
                if (!input.TryGetProperty("milliseconds", out var waitElement)
                    || waitElement.ValueKind is not JsonValueKind.Number
                    || !waitElement.TryGetInt32(out var milliseconds)
                    || milliseconds <= 0
                    || milliseconds > MaxWaitMilliseconds)
                {
                    issue = new ToolInputIssue(
                        "invalid_milliseconds",
                        $"'wait' requires integer 'milliseconds' in (0, {MaxWaitMilliseconds}]");
                    return false;
                }
                action = new BrowserWaitAction(milliseconds);
                return true;
            case "read_text":
                action = new BrowserReadTextAction();
                return true;
            default:
                issue = new ToolInputIssue(
                    "unknown_action",
                    $"Unknown browser action '{verb}'; expected one of: {string.Join(", ", Verbs)}");
                return false;
        }
    }

    private static bool TryGetNonEmptyString(JsonElement input, string name, out string value)
    {
        value = string.Empty;
        if (input.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
            && element.GetString() is { } parsed
            && !string.IsNullOrWhiteSpace(parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static int GetInt32OrDefault(JsonElement input, string name) =>
        input.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.Number
            && element.TryGetInt32(out var value)
            ? value
            : 0;

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
