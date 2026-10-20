using System.Globalization;
using System.Text.Json;
using Alcoler.Adapters.Web;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class WebSearchTool : ITool, IDisposable
{
    private const int MaxSourceLinks = 20;
    private const string SourceReminder =
        "REMINDER: You MUST include the sources above in your response to the user using markdown hyperlinks.";

    private readonly BraveWebSearchClient _client;
    private readonly TimeProvider _clock;

    public WebSearchTool(string apiKey, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _client = new BraveWebSearchClient(apiKey, handler);
        // The description is computed once at construction so a long-lived process never
        // advertises a stale month to the model.
        _clock = clock ?? TimeProvider.System;
        Contract = new ToolContract(
            "WebSearch",
            BuildDescription(_clock.GetUtcNow()),
            ToolInput.ParseSchema("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "query": { "type": "string", "minLength": 2, "description": "The search query to use" },
                "allowed_domains": { "type": "array", "items": { "type": "string" }, "description": "Only include search results from these domains" },
                "blocked_domains": { "type": "array", "items": { "type": "string" }, "description": "Never include search results from these domains" }
              },
              "required": ["query"],
              "additionalProperties": false
            }
            """));
    }

    public ToolContract Contract { get; }

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Network,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true,
        DefaultTimeoutMs: 30_000);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (HasDomains(input, "allowed_domains") && HasDomains(input, "blocked_domains"))
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "blocked_domains",
                "allowed_domains and blocked_domains cannot both be specified"));
        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = ToolInput.String(context.Input, "query");
        var allowed = ReadDomains(context.Input, "allowed_domains");
        var blocked = ReadDomains(context.Input, "blocked_domains");

        IReadOnlyList<WebSearchResult> results;
        try
        {
            results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        var filtered = results
            .Where(result => !MatchesAnyDomain(result.Url, blocked))
            .Where(result => allowed.Count == 0 || MatchesAnyDomain(result.Url, allowed))
            .ToList();

        if (filtered.Count == 0)
            return ToolExecutionResult.Text(string.Join("\n",
                $"Web search results for query: \"{query}\"",
                "",
                "Links:",
                "- No links found.",
                "",
                SourceReminder).Trim());

        var lines = new List<string> { $"Web search results for query: \"{query}\"", "" };
        var summary = string.Join("\n", filtered
            .Select(result => result.Description?.Trim())
            .Where(description => !string.IsNullOrEmpty(description)));
        if (summary.Length > 0)
            lines.AddRange(["Summary:", summary, ""]);

        lines.Add("Links:");
        foreach (var link in DedupedByUrl(filtered).Take(MaxSourceLinks))
            lines.Add($"- [{(string.IsNullOrWhiteSpace(link.Title) ? link.Url : link.Title!)}]({link.Url})");
        lines.AddRange(["", SourceReminder]);

        return ToolExecutionResult.Text(string.Join("\n", lines).Trim());
    }

    public void Dispose() => _client.Dispose();

    private static string BuildDescription(DateTimeOffset now) => string.Join("\n",
        "Search the web. Returns result blocks with titles and URLs. US-only.",
        "",
        $"- The current month is {now.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} — use this when searching for recent information.",
        "- `allowed_domains` / `blocked_domains` filter results.",
        "- After answering from results, end with a \"Sources:\" list of the URLs you used as markdown links.");

    private static bool HasDomains(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static List<string> ReadDomains(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(domain => domain.Length > 0)
                .ToList()
            : [];

    private static bool MatchesAnyDomain(string url, IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return false;
        var host = uri.Host;
        return domains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || (host.Length > domain.Length + 1
                && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<WebSearchResult> DedupedByUrl(IEnumerable<WebSearchResult> results)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
            if (seen.Add(result.Url))
                yield return result;
    }
}
