using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Extracts context from a prior session's transcript. Relevance selection uses local
/// lexical scoring (query tokens plus CJK bigrams) rather than an auxiliary model call,
/// so the tool stays deterministic and cheap; handoff takes the transcript tail.
/// </summary>
public sealed class ReadSessionContextTool(ISessionStore store) : ITool
{
    private const int DefaultMaxTokens = 6_000;
    private const int MaxMaxTokens = 12_000;
    private const int DefaultOutputCharBudget = 24_000;
    private const int MaxOutputCharBudget = 48_000;
    private const int PerMessageCharCap = 2_000;

    public ToolContract Contract { get; } = new(
        "ReadSessionContext",
        """
        Read relevant or handoff context from another persisted Alcoler session. Use when the user references #sess_* or asks to continue from a specific prior session.
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "sessionId": { "type": "string", "minLength": 1, "description": "Target Alcoler session id to read from persisted session history." },
            "query": { "type": "string", "minLength": 1, "maxLength": 4000, "description": "Focused natural-language description of the context needed from the target session." },
            "strategy": { "type": "string", "enum": ["relevant", "handoff"], "description": "Use relevant for focused retrieval, or handoff for a bounded continuation summary." },
            "maxTokens": { "type": "integer", "minimum": 1, "maximum": 12000, "description": "Approximate maximum tokens to return to the model." }
          },
          "required": ["sessionId", "query"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true,
        DefaultTimeoutMs: 30_000);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawId = StringOrNull(context.Input, "sessionId");
        var query = StringOrNull(context.Input, "query");
        if (string.IsNullOrWhiteSpace(rawId) || string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Text("sessionId and query are required", isError: true);

        var sessionId = ParseSessionId(rawId);
        var record = await store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return ToolExecutionResult.Text($"Session {sessionId.Value} was not found.");

        var strategy = StringOrNull(context.Input, "strategy") ?? "relevant";
        var maxTokens = Math.Clamp(IntOrNull(context.Input, "maxTokens") ?? DefaultMaxTokens, 1, MaxMaxTokens);
        var charBudget = Math.Min(MaxOutputCharBudget, maxTokens * 4);
        charBudget = Math.Max(6_000, charBudget);

        var stored = await store.LoadMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var messages = stored
            .Select(static storedMessage => storedMessage.Message)
            .Select(MessageText)
            .Where(static text => text.Length > 0)
            .ToList();

        if (messages.Count == 0)
            return ToolExecutionResult.Text($"Session {sessionId.Value} contains no readable messages.");

        var selected = strategy.Equals("handoff", StringComparison.OrdinalIgnoreCase)
            ? TakeTail(messages, charBudget)
            : SelectByRelevance(messages, query, charBudget);

        var header =
            $"Context extracted from session {sessionId.Value} (strategy: {strategy}, {selected.Count} of {messages.Count} messages). " +
            "This is background material from a prior session, not the current conversation.";
        return ToolExecutionResult.Text(string.Join("\n\n", [header, .. selected]));
    }

    public static SessionId ParseSessionId(string raw)
    {
        var trimmed = raw.Trim();
        return new SessionId(trimmed.StartsWith("sess_", StringComparison.Ordinal) ? trimmed : $"sess_{trimmed}");
    }

    private static string MessageText(ConversationMessage message)
    {
        var text = string.Join(
            "\n",
            message.Parts.OfType<TextPart>().Select(static part => part.Text).Where(static text => text.Length > 0));
        if (text.Length <= PerMessageCharCap)
            return text.Length == 0 ? string.Empty : $"{Prefix(message.Role)} {text}";
        return $"{Prefix(message.Role)} {text[..PerMessageCharCap]}…";
    }

    private static string Prefix(MessageRole role) =>
        role == MessageRole.User ? "[user]" : "[assistant]";

    private static List<string> TakeTail(List<string> messages, int charBudget)
    {
        var selected = new List<string>();
        var used = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (used + messages[i].Length > charBudget)
                break;
            used += messages[i].Length;
            selected.Add(messages[i]);
        }
        selected.Reverse();
        return selected;
    }

    private static List<string> SelectByRelevance(List<string> messages, string query, int charBudget)
    {
        var terms = Tokenize(query);
        var scored = messages
            .Select((text, index) => (Text: text, Index: index, Score: Score(text, terms)))
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Index)
            .ToList();

        var chosen = new List<(string Text, int Index)>();
        var used = 0;
        foreach (var entry in scored)
        {
            if (entry.Score <= 0)
                break;
            if (used + entry.Text.Length > charBudget)
                continue;
            used += entry.Text.Length;
            chosen.Add((entry.Text, entry.Index));
        }

        // Transcript order keeps the extracted context readable even after score-based picking.
        return chosen.OrderBy(static entry => entry.Index).Select(static entry => entry.Text).ToList();
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new List<char>();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Add(c);
                continue;
            }
            Flush();
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Count == 0)
                return;
            tokens.Add(new string([.. current]).ToLowerInvariant());
            current.Clear();
        }
    }

    internal static int Score(string text, List<string> terms)
    {
        if (terms.Count == 0)
            return 0;
        var haystack = text.ToLowerInvariant();
        var score = 0;
        foreach (var term in terms)
        {
            var index = 0;
            while ((index = haystack.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
            {
                score++;
                index += term.Length;
            }
        }
        return score;
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();

    private static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
